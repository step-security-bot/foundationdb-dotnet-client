#region Copyright Doxense 2010-2020
//
// All rights are reserved. Reproduction or transmission in whole or in part, in
// any form or by any means, electronic, mechanical or otherwise, is prohibited
// without the prior written consent of the copyright owner.
//
#endregion

namespace Doxense.Web
{
	using System;
	using System.Collections.Specialized;
	using System.Diagnostics.CodeAnalysis;
	using System.Globalization;
	using System.IO;
	using System.Text;
	using Doxense.Serialization;
	using JetBrains.Annotations;

	/// <summary>Classe helper pour l'encode/d�codage d'URLs</summary>
	/// <remarks>Elle a pour but de simuler HttpUtility d�finie dans System.Web.dll qui n'est pas accessible dans le .NET Client Profile</remarks>
	public static class UrlEncoding
	{
		private static class Tokens
		{
			public const string True = "true";
			public const string False = "false";

			public const string FormatR = "R";
			public const string FormatDate = "yyyyMMdd";
			public const string FormatDateTime = "yyyyMMddHHmmss";
			public const string FormatDateTimeMillis = "yyyyMMddHHmmssfff";
		}

		#region Static Members...

		/// <summary>Map qui contient la cat�gorie de chaque caract�re d'une URL</summary>
		private static readonly byte[] s_charMap = InitializeCharMap();

		private static readonly char[] s_charHex = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f' };

		private const byte CLEAN = 0; // Jamais modifi�
		private const byte PATH = 1; // Normalement encod� en Percent, mais traitement sp�cial ?
		private const byte SPACE = 2; // Soit '+', soit '%20'
		private const byte DELIM = 3; // D�limiteur de chemin ('/', ':', ...)
		private const byte INVALID = 4; // "%XX"

		#endregion

		#region Public Methods...

		/// <summary>D�code une cha�ne de texte encod�e comme une URL (%XX)</summary>
		/// <param name="value">Cha�ne contenant du texte encod�</param>
		/// <param name="encoding">Encoding utilis� (par d�faut UTF-8 si null)</param>
		/// <returns>Cha�ne d�cod�e</returns>
		[Pure]
		public static string Decode(string? value, Encoding? encoding = null)
		{
			return Decode(value, 0, value?.Length ?? 0, encoding);
		}

		/// <summary>D�code une section d'une cha�ne de texte encod�e comme une URL (%XX)</summary>
		/// <param name="value">Cha�ne contenant une URI ou tout autre texte encod� comme une URL</param>
		/// <param name="offset">Offset � partir du d�but de la cha�ne</param>
		/// <param name="count">Nombre de caract�res � d�coder</param>
		/// <param name="encoding">Encoding utilis� (par d�faut UTF-8 si null)</param>
		/// <returns>Section de la cha�ne d�cod�e</returns>
		[Pure]
		public static string Decode(string? value, int offset, int count, Encoding? encoding = null)
		{
			if (value == null || count <= 0)
			{
				return string.Empty;
			}
			if (NeedsDecoding(value, offset, count))
			{
				return DecodeString(value, offset, count, encoding);
			}
			if (offset == 0 && count == value.Length)
			{
				return value;
			}
			return value.Substring(offset, count);
		}

		/// <summary>Parse une QueryString, et passe le couple (attribut, valeur) � une lambda</summary>
		/// <typeparam name="TState">Type de l'�tat pass� au handler (buffer, liste, ...)</typeparam>
		/// <param name="qs">QueryString � parser (sous la forme 'name1=value1&amp;name2=value2&amp;...')</param>
		/// <param name="state">Variable transmise � chaque appel du handler</param>
		/// <param name="handler">Action appel�e pour chaque param�tre, avec le couple name/value (d�cod�s). La value est null si le param�tre n'a pas de section '=xxxx'</param>
		/// <param name="encoding">Encoding utilis� (par d�faut UTF-8 si null)</param>
		[Pure]
		internal static TState ParseQueryString<TState>(string? qs, TState state, Action<TState, string, string?> handler, Encoding? encoding = null)
		{
			int length;
			if (qs == null || (length = qs.Length) == 0) return state;

			// on d�marre du d�but, sauf s'il y a un '?'
			int start = 0;
			if (qs[0] == '?') ++start; // skip

			for (int i = start; i < length; i++)
			{
				start = i;
				int end = -1;

				// recherche la fin du couple 'attr=name' (termin� par un '&' ou la fin de la cha�ne)
				while (i < length)
				{
					char c = qs[i];
					if (c == '=')
					{ // fin du nom, d�but de la valeur
						if (end < 0) end = i;
					}
					else if (c == '&')
					{ // fin du couple
						break;
					}
					++i;
				}

				if (start == i)
				{ // un "&" qui se balade tout seul ??
					continue;
				}

				if (end < 0)
				{ // pas de valeur
					handler(state, Decode(qs, start, i - start, encoding), null);
				}
				else
				{ // valeur pr�sente
					handler(state, Decode(qs, start, end - start, encoding), Decode(qs, end + 1, i - end - 1, encoding));
				}
			}
			return state;
		}

		/// <summary>Parse une QueryString, et retourne la liste des param�tres trouv�s</summary>
		/// <param name="qs">QueryString � parser (sous la forme 'name1=value1&amp;name2=value2&amp;...')</param>
		/// <param name="encoding">Encoding utilis� (par d�faut UTF-8 si null)</param>
		/// <returns>NameValueCollection contenant les param�tres de la querystring</returns>
		/// <remarks>"foo&..." contiendra null, "foo=&..." contiendra String.Empty</remarks>
		[Pure]
		public static NameValueCollection ParseQueryString(string? qs, Encoding? encoding = null)
		{
			return ParseQueryString(qs, new NameValueCollection(), (values, name, value) => values.Add(name, value), encoding);
		}

		/// <summary>D�code une cha�ne de texte contenant une URL</summary>
		/// <param name="value">Cha�ne � d�coder</param>
		/// <param name="offset">Offset � partir du d�but de la cha�ne</param>
		/// <param name="count">Nombre de caract�res � d�coder</param>
		/// <param name="encoding">Encoding utilis� (par d�faut UTF-8 si null)</param>
		/// <returns>Section de l'url d�cod�e</returns>
		[Pure]
		private static string DecodeString(string value, int offset, int count, Encoding? encoding)
		{
			encoding ??= Encoding.UTF8;

			// s'il n'y a rien � d�coder, la taille du buffer de sortie est la m�me que celle de la string
			// s'il y a des choses, elle sera plus petite, avec une taille de 1/3 dans le pire des cas

			unsafe
			{
				fixed (char* chars = value)
				{
					if (count > 1024)
					{ // trop gros pour allouer sur la stack
						// => on alloue en m�moire
						var buffer = new byte[count];
						int size;
						fixed (byte* bytes = buffer)
						{
							size = DecodeBytes(chars, offset, count, bytes, encoding);
						}
						return encoding.GetString(buffer, 0, size);
					}
					else
					{ // ca peut passer sur la stack
						// d�code dans un buffer sur la stack
						byte* bytes = stackalloc byte[count];
						int numBytes = DecodeBytes(chars, offset, count, bytes, encoding);
						// d�termine le nb de caract�res
						int numChars = encoding.GetCharCount(bytes, numBytes);
						// alloue le buffer de chars (sur la stack aussi)
						char* result = stackalloc char[numChars];
						int n = encoding.GetChars(bytes, numBytes, result, numChars);
						// retourne la string correspondante
						return new string(result, 0, n);
					}
				}
			}
		}

		/// <summary>D�termine si la cha�ne n�cessite d'�tre d�cod�e (de mani�re pessimiste)</summary>
		/// <param name="value">Cha�ne de texte pr�sente dans une URL</param>
		/// <param name="offset"></param>
		/// <param name="count"></param>
		/// <returns>True si la cha�ne contient (�ventuellement) des caract�res � encoder, false si elle est propre.</returns>
		[ContractAnnotation("value:null => false")]
		private static bool NeedsDecoding(string? value, int offset, int count)
		{
			if (value != null)
			{
				int p = offset;
				while (count-- > 0)
				{
					char c = value[p++];
					if (c == '%' || c == '+') return true;
				}
			}
			return false;
		}

		/// <summary>Retourne la valeur d�cimale d'une digit hexa d�cimal, ou -1 si ce n'en est pas un</summary>
		/// <param name="c">0-9, A-F, a-f</param>
		/// <returns>0-15, ou -1 si ce n'est pas un digit hexa d�cimal</returns>
		private static int DecodeHexDigit(char c)
		{
			// on accepte A-F, a-f et 0-9
			if (c < '0') return -1;
			if (c <= '9') return c - 48;
			if (c >= 'A' && c <= 'F') return c - 55;
			if (c >= 'a' && c <= 'f') return c - 87;
			return -1;
		}

		/// <summary>D�code une buffer de caract�res contenant une URL, vers un buffer d'octets (pour d�codage UTF-8)</summary>
		/// <param name="value">Buffer contenant les caract�res de l'URL</param>
		/// <param name="offset">Offset dans le d�but du buffer</param>
		/// <param name="count">Nombre de caract�res � d�coder</param>
		/// <param name="bytes">Buffer de sortie o� �crire les octets d�cod�s</param>
		/// <param name="encoding">Encoding utilis� (UTF-8 par d�faut si null)</param>
		/// <returns>Nombre d'octets �crit dans le buffer de sortie</returns>
		private static unsafe int DecodeBytes(char* value, int offset, int count, byte* bytes, Encoding encoding)
		{
			encoding ??= Encoding.UTF8;

			//IMPORTANT: on se repose sur le fait que l'appelant a tailler 'bytes' suffisamment grand pour qu'il n'y ait pas d'overflow !!!

			int pDst = 0;
			int pSrc = offset;
			while(count-- > 0)
			{
				byte val = (byte) value[pSrc++];
				if (val == '+')
				{ // Space
					val = 32;
				}
				else if (val == '%' && count >= 2)
				{ // Percent-Encoded ?

					// trois possibilit�s:
					// - '%XX' : percent encoded
					// - '%uXXXX' : unicode encoded
					// - un '%' mal encod� qu'on doit laisser passer

					if (value[pSrc] == 'u' && count >= 5)
					{ // '%uXXXX' ?
						// values[pSrc] == 'u'
						int a = DecodeHexDigit(value[pSrc + 1]);
						int b = DecodeHexDigit(value[pSrc + 2]);
						int c = DecodeHexDigit(value[pSrc + 3]);
						int d = DecodeHexDigit(value[pSrc + 4]);
						if (a >= 0 && b >= 0 && c >= 0 && d >= 0)
						{ // les deux sont en hexa, on accepte le caract�re

							// grah, le pb c'est qu'il faut qu'on rajoute les bytes correspondant � de l'UTF-8 :(
							char ch = (char) ((a << 12) | (b << 8)  | (c << 4) | d);
							// "%uXXXX" fait 6 bytes, et normalement, il n'y a rien qui peut faire plus de 5 bytes une fois encod� en UTF-8
							int n = encoding.GetBytes(&ch, 1, bytes + pDst, count);
							pDst += n;
							pSrc += 5;
							count -= 5;
							continue; // => next
						}
					}
					else
					{ // '%XX'
						// les deux suivants doivent �tre en hexa
						int hi = DecodeHexDigit(value[pSrc]);
						int lo = DecodeHexDigit(value[pSrc + 1]);
						if (hi >= 0 && lo >= 0)
						{ // les deux sont en hexa, on accepte le caract�re
							bytes[pDst++] = (byte)((hi << 4) | lo);
							pSrc += 2;
							count -= 2;
							continue; // => next
						}
					}
					// sinon c'est un encodage foireux, on le laisse passer tel quel
				}
				bytes[pDst++] = val;
			}

			return pDst;
		}

		#region Uri...

		/// <summary>Encode correctement une URI</summary>
		/// <param name="value">Uri � encoder correctement</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>Uri encod�e correctement</returns>
		/// <remarks>Ne touche pas � la query string s'il y en a une !</remarks>
		/// <example>EncodeUri("http://server/path to the/file.ext?blah=xxxx") => "http://server/path%20to%20the/file.ext?blah=xxx"</example>
		[Pure]
		public static string EncodeUri(string? value, Encoding? encoding = null)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}

			// ATTENTION: on ne doit pas toucher � la QueryString !
			int p = value.IndexOf('?');
			if (p >= 0)
			{ // appel r�cursif pour n'encoder que le path, en recollant la QueryString
				return EncodeUri(value.Substring(0, p)) + value.Substring(p);
			}

			if (!NeedsEncoding(value))
			{ // pas besoin d'encoder
				return value;
			}

			return EncodeString(value, encoding, true, true);
		}

		/// <summary>Ajoute une URI � un StringBuilder, en l'encodant correctement</summary>
		/// <param name="builder">Builder o� ajoute l'URI encod�e</param>
		/// <param name="value">Uri � encoder correctement</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>StringBuilder, pour cha�nage</returns>
		/// <example>AppendUriTo(sb, "http://server/path to the/file.ext?blah=xxxx") ~= sb.Append("http://server/path%20to%20the/file.ext?blah=xxx")</example>
		public static StringBuilder AppendUriTo(StringBuilder builder, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				// ATTENTION: on ne doit pas toucher � la QueryString !
				int p = value.IndexOf('?');
				string? suffix = null;
				if (p >= 0)
				{ // appel r�cursif pour n'encoder que le path, en recollant la QueryString
					suffix = value.Substring(p);
					value = value.Substring(0, p);
				}

				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					builder.Append(value);
				}
				else
				{
					builder.Append(EncodeString(value, encoding, true, true));
				}

				// rajoutes la querystring s'il y en avait une
				if (suffix != null) builder.Append(suffix);
			}
			return builder;
		}

		/// <summary>Ecrit une URI dans un TextWriter, en l'encodant proprement</summary>
		/// <param name="writer">Writer o� �crire l'URI encod�e</param>
		/// <param name="value">Uri � encoder correctement</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>TextWriter, pour cha�nage</returns>
		/// <example>WriteUriTo(writer, "http://server/path to the/file.ext?blah=xxxx") ~= writer.Write("http://server/path%20to%20the/file.ext?blah=xxx")</example>
		public static TextWriter WriteUriTo(TextWriter writer, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				// ATTENTION: on ne doit pas toucher � la QueryString !
				int p = value.IndexOf('?');
				string? suffix = null;
				if (p >= 0)
				{ // appel r�cursif pour n'encoder que le path, en recollant la QueryString
					suffix = value.Substring(p);
					value = value.Substring(0, p);
				}

				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					writer.Write(value);
				}
				else
				{
					writer.Write(EncodeString(value, encoding, true, true));
				}

				// rajoutes la querystring s'il y en avait une
				if (suffix != null) writer.Write(suffix);
			}
			return writer;
		}

		#endregion

		#region Path...

		/// <summary>Encode une valeur qui sera utilis�e comme segment du chemin d'une URI</summary>
		/// <param name="value">Valeur � encoder correctement (' ' => '%20')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>Texte pouvant �tre int�gr� dans le chemin d'une URI</returns>
		/// <example>EncodePath("foo bar/baz") => "foo%20bar%2fbaz"</example>
		[Pure]
		public static string EncodePath(string? value, Encoding? encoding = null)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}
			if (!NeedsEncoding(value))
			{ // pas besoin d'encoder
				return value;
			}
			return EncodeString(value, encoding, true, false);
		}

		[Pure]
		public static string EncodePathObject(object? value, Encoding? encoding = null)
		{
			return EncodePath(ObjectToString(value), encoding);
		}

		/// <summary>Concat�ne une valeur qui sera utilis�e comme segment du chemin d'une URI</summary>
		/// <param name="builder">Builder o� ajouter la valeur encod�e</param>
		/// <param name="value">Valeur � encoder correctement (' ' => '%20')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>StringBuilder, pour cha�nage</returns>
		/// <example>AppendPathTo(sb, "foo bar/baz") ~= sb.Append("foo%20bar%2fbaz")</example>
		public static StringBuilder AppendPathTo(StringBuilder builder, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					builder.Append(value);
				}
				else
				{
					builder.Append(EncodeString(value, encoding, true, false));
				}
			}
			return builder;
		}

		/// <summary>Ecrit une valeur qui sera utilis�e comme segment du chemin d'une URI</summary>
		/// <param name="writer">Writer o� �crire la valeur encod�e</param>
		/// <param name="value">Valeur � encoder correctement (' ' => '%20')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>TextWriter, pour cha�nage</returns>
		/// <example>WritePathTo(writer, "foo bar/baz") ~= writer.Write("foo%20bar%2fbaz")</example>
		public static TextWriter WritePathTo(TextWriter writer, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					writer.Write(value);
				}
				else
				{
					writer.Write(EncodeString(value, encoding, true, false));
				}
			}
			return writer;
		}

		#endregion

		#region Data...

		[Pure]
		private static string ObjectToString(object? value)
		{
			// most frequent types
			if (value == null) return String.Empty;
			if (value is string s) return s;

			var type = value.GetType();
			if (type.IsPrimitive)
			{
				// Attention: GetTypeCode retourne 'TypeCode.Int32' pour une Enum !
				switch (Type.GetTypeCode(type))
				{
					case TypeCode.Boolean: return ((bool)value) ? Tokens.True : Tokens.False;
					case TypeCode.Char: return new string((char)value, 1);
					case TypeCode.SByte: return StringConverters.ToString((int)(sbyte)value);
					case TypeCode.Byte: return StringConverters.ToString((int) (byte)value);
					case TypeCode.Int16: return StringConverters.ToString((int) (short)value);
					case TypeCode.UInt16: return StringConverters.ToString((int) (ushort)value);
					case TypeCode.Int32: return StringConverters.ToString((int)value);
					case TypeCode.UInt32: return StringConverters.ToString((uint)value);
					case TypeCode.Int64: return StringConverters.ToString((long)value);
					case TypeCode.UInt64: return ((ulong)value).ToString(null, CultureInfo.InvariantCulture);
					case TypeCode.Single: return ((float)value).ToString(Tokens.FormatR, CultureInfo.InvariantCulture);
					case TypeCode.Double: return ((double)value).ToString(Tokens.FormatR, CultureInfo.InvariantCulture);
					//note: decimal n'est pas primitive !
				}
			}

			if (value is TimeSpan ts)
			{ // TimeSpan => nombre de secondes
				return ts.TotalSeconds.ToString(Tokens.FormatR, CultureInfo.InvariantCulture);
			}

			if (value is DateTime date)
			{ // Date => YYYYMMDD[HHMMSS[fff]]
				var time = date.TimeOfDay;
				if (time == TimeSpan.Zero) return date.ToString(Tokens.FormatDate);
				if (time.Milliseconds == 0) return date.ToString(Tokens.FormatDateTime);
				return date.ToString(Tokens.FormatDateTimeMillis);
			}

			if (value is decimal dec)
			{
				return dec.ToString(null, CultureInfo.InvariantCulture);
			}

			if (value is Enum e)
			{
				return e.ToString();
			}

			if (value is IFormattable fmt)
			{
				return fmt.ToString(null, CultureInfo.InvariantCulture);
			}

			// on croise les doigts...
			return value.ToString();
		}

		/// <summary>Encode une valeur qui sera utilis�e comme valeur dans une QueryString</summary>
		/// <param name="value">Valeur � encoder correctement (' ' => '+')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>Texte pouvant �tre utilis� comme valeur dans une QueryString</returns>
		/// <example>EncodeData("foo bar/baz") => "foo+bar%2fbaz"</example>
		[Pure]
		public static string EncodeData(string? value, Encoding? encoding = null)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}
			if (!NeedsEncoding(value))
			{ // pas besoin d'encoder
				return value;
			}
			return EncodeString(value, encoding, path: false, delims: false);
		}

		[Pure]
		public static string EncodeDataObject(object? value, Encoding? encoding = null)
		{
			return EncodeData(ObjectToString(value), encoding);
		}

		/// <summary>Concat�ne une valeur qui sera utilis�e comme valeur dans une QueryString</summary>
		/// <param name="builder">Builder o� ajouter la valeur encod�e</param>
		/// <param name="value">Valeur � encoder correctement (' ' => '+')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>StringBuilder, pour cha�nage</returns>
		/// <example>AppendDataTo(sb, "foo bar/baz") ~= sb.Append("foo+bar%2fbaz")</example>
		public static StringBuilder AppendDataTo(StringBuilder builder, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					builder.Append(value);
				}
				else
				{
					//REVIEW: PERF: �crire une version de EncodeString qui append directement dans un StringBuilder (sans allouer de string)
					builder.Append(EncodeString(value, encoding, path: false, delims: false));
				}
			}
			return builder;
		}

		/// <summary>Ecrit une valeur qui sera utilis�e comme valeur dans une QueryString</summary>
		/// <param name="writer">Writer o� �crire la valeur encod�e</param>
		/// <param name="value">Valeur � encoder correctement (' ' => '+')</param>
		/// <param name="encoding">Encoding optionnel (UTF-8 par d�faut)</param>
		/// <returns>TextWriter, pour cha�nage</returns>
		/// <example>WriteDataTo(writer, "foo bar/baz") ~= writer.Write("foo+bar%2fbaz")</example>
		public static TextWriter WriteDataTo(TextWriter writer, string? value, Encoding? encoding = null)
		{
			if (!string.IsNullOrEmpty(value))
			{
				if (!NeedsEncoding(value))
				{ // pas besoin d'encoder
					writer.Write(value);
				}
				else
				{
					//REVIEW: PERF: �crire une version de EncodeString qui append directement dans un TextWriter (sans allouer de string)
					writer.Write(EncodeString(value, encoding, path: false, delims: false));
				}
			}
			return writer;
		}

		#endregion

		#endregion

		#region Internal Helpers...

		/// <summary>G�n�re la map de conversion des caract�res</summary>
		[Pure]
		private static byte[] InitializeCharMap()
		{
			// Note: vu qu'on encode la version UTF-8 (bytes), on ne travaille que sur des octets !
			var map = new byte[256];
			for (int i = 0; i < map.Length; i++)
			{
				char c = (char)i;

				if (IsNeverEncoded(c))
					map[i] = CLEAN;
				else if (IsPathDelimOrSpecial(c))
					map[i] = DELIM;
				else if (IsValidOnlyInPath(c))
					map[i] = PATH;
				else if (c == ' ')
					map[i] = SPACE;
				else
					map[i] = INVALID;
			}
			return map;
		}

		[Pure]
		private static bool IsNeverEncoded(char c)
		{
			// ces caract�res ne seront JAMAIS encod�s !
			return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~';
		}

		[Pure]
		private static bool IsValidOnlyInPath(char c)
		{
			// ces caract�res ne sont pas encod�s dans une URI
			return c == '!' || c == '*' || c == '\'' || c == '(' || c == ')' || c == ';' || c == ':' || c == '@' || c == '&' || c == '=' || c == '+' || c == '$' || c == ',' || c == '/' || c == '?' || c == '#' || c == '[' || c == ']'
				|| c == '\\' || c == '"' || c == '<' || c == '>' || c == '^' || c == '`' || c == '{' || c == '|' || c == '}';
		}

		[Pure]
		private static bool IsPathDelimOrSpecial(char c)
		{
			// ces caract�res correspondant a des s�parateurs de path (qui sont encod�s si on g�n�re un path)
			return c == ':' || c == '/' || c == '?' || c == '#' || c == '[' || c == ']' || c == '@' || c == '%';
		}

		/// <summary>D�termine si la cha�ne n�cessite d'�tre encod�e (de mani�re pessimiste)</summary>
		/// <param name="value">Cha�ne de texte � ins�rer dans une URL</param>
		/// <returns>True si la cha�ne contient (�ventuellement) des caract�res � encoder, false si elle est propre.</returns>
		[Pure, ContractAnnotation("null => false")]
		private static bool NeedsEncoding(string? value)
		{
			if (value != null)
			{
				byte[] map = s_charMap;
				int n = value.Length;
				for (int i = 0; i < n; i++)
				{
					char c = value[i];
					if (c > 255 || map[c] != CLEAN) return true;
				}
			}
			return false;
		}

		/// <summary>Encode une cha�ne de texte</summary>
		/// <param name="value">Cha�ne de texte � encoder</param>
		/// <param name="encoding">Encoding � utiliser (UTF-8 par d�faut)</param>
		/// <param name="path">Indique si on est dans la section 'PATH' (true) d'une URI (ou false si on est dans la Query String)</param>
		/// <param name="delims">Indique si on doit laisser passer les d�limiteurs de path</param>
		/// <returns>Cha�ne encod�e</returns>
		private static unsafe string EncodeString(string value, Encoding? encoding, bool path, bool delims)
		{
			var bytes = (encoding ?? Encoding.UTF8).GetBytes(value);
			int size = ComputeEncodedSize(bytes, path, delims);

			// on alloue directement la cha�ne, en modifiant "in place" son contenu
			var result = new string('\0', size);
			fixed (char* chars = result)
			{
				EncodeChars(bytes, 0, bytes.Length, chars, 0, path, delims);
			}
			return result;
		}

		/// <summary>Calcule la taille (en caract�res) n�cessaire pour encoder une cha�ne de texte</summary>
		/// <param name="bytes">Octets repr�sentant la cha�ne de texte initiale (en UTF-8 par exemple)</param>
		/// <param name="path">Indique si on est dans la section 'PATH' (true) d'une URI (ou false si on est dans la Query String)</param>
		/// <param name="delims">Indique si on doit laisser passer les d�limiteurs de path</param>
		/// <returns></returns>
		[Pure]
		private static int ComputeEncodedSize(byte[] bytes, bool path, bool delims)
		{
			int extra = 0;
			byte[] map = s_charMap;
			foreach (byte b in bytes) {
				byte category;
				//note: comme la valeur est un octet, elle est forc�ment <= 255, donc pas la peine de faire de bound-check sur map !
				if ((category = map[b]) != CLEAN)
				{
					switch (category)
					{
						case PATH:
						{
							// il n'est pas modifi� dans un PATH
							if (!path) ++extra;
							break;
						}
						case SPACE:
						{
							// dans la querystring, on n'augmente pas la taille !
							if (path) ++extra;  // %20
							break;
						}
						case DELIM:
						{
							// il n'est pas modifi� si on sanitize
							if (!delims) ++extra; // %25
							break;
						}
						case INVALID:
						{ // passe de 1 � 3 ("%XX")
							++extra;
							break;
						}
					}
				}
			}
			return bytes.Length + (extra << 1);
		}

		/// <summary>Encode un buffer contenant du texte UTF-8 vers un tableau de chars</summary>
		/// <returns>Offset dans le buffer suivant le dernier caract�re encod�</returns>
		private static unsafe void EncodeChars(byte[]? value, int offset, int count, char* chars, int index, bool path, bool delims)
		{
			if (value == null || value.Length == 0)
			{
				return;
			}

			char[] hexes = s_charHex; // en encode en minuscules
			byte[] map = s_charMap;

			int p = index;
			for (int i = 0; i < count; i++)
			{
				byte c = value[offset + i];
				//note: comme la valeur est un octet, elle est forc�ment <= 255, donc pas la peine de faire de bound-check sur map !
				switch (map[c])
				{
					case CLEAN:
					{ // caract�re qui n'est jamais encod�
						chars[p++] = (char) c;
						break;
					}
					case PATH:
					{ // caract�re qui n'est autoris� que dans un path
						if (path)
						{ // on ne laisse passer que dans le path
							chars[p++] = (char) c;
						}
						else
						{ // sinon on encode
							chars[p] = '%';
							chars[p + 1] = hexes[(c >> 4) & 0xF];
							chars[p + 2] = hexes[c & 0xF];
							p += 3;
						}
						break;
					}
					case SPACE:
					{ // Espace
						if (path)
						{ // dans le path
							chars[p] = '%';
							chars[p + 1] = '2';
							chars[p + 2] = '0';
							p += 3;
						}
						else
						{ // dans la query string
							chars[p++] = '+';
						}
						break;
					}
					case DELIM:
					{ // d�limiteur de path
						if (delims)
						{ // on ne laisse passer que si on nettoie une URI
							chars[p++] = (char) c;
						}
						else
						{ // sinon on encode
							chars[p] = '%';
							chars[p + 1] = hexes[(c >> 4) & 0xF];
							chars[p + 2] = hexes[c & 0xF];
							p += 3;
						}
						break;
					}
					case INVALID:
					{ // caract�re encod� quoi qu'il arrive
						// '%xx' (en minuscules!)
						chars[p] = '%';
						chars[p + 1] = hexes[(c >> 4) & 0xF];
						chars[p + 2] = hexes[c & 0xF];
						p += 3;
						break;
					}
				}
			}
		}

		#endregion

	}

}
