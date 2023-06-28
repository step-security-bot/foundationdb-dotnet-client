#region Copyright Doxense 2003-2020
//
// All rights are reserved. Reproduction or transmission in whole or in part, in
// any form or by any means, electronic, mechanical or otherwise, is prohibited
// without the prior written consent of the copyright owner.
//
#endregion

//#define FULL_DEBUG

namespace Doxense.Serialization.Asn1
{
	using System;
	using System.Runtime.CompilerServices;
	using System.Text;
	using Doxense.Diagnostics.Contracts;
	using Doxense.Memory;
	using JetBrains.Annotations;

	//TODO: cette classe �tait normalement utilis�e par SNMP, mais est utilis�e par d'autres pour g�rer l'encodage ASN.1
	// => elle devrait �tre "d�sp�cialis�e" pour devenir le plus g�n�rique possible!

	/// <summary>Routines statiques d'encodage/d�codage des r�gles ASN.1</summary>
	public static class Asn1
	{
		public const string TOKENS_PREFIX_1_2 = "1.2";
		public const string TOKENS_PREFIX_1_3 = "1.3";

		#region Encodage et decodage des longueurs...

		/// <summary>Calcule la longueur d'un champ "longueur" se trouvant dans un byte[]
		/// (c.a.d. souvent dans un paquet ou datablock brut)</summary>
		/// <param name="src">tableau source contenant le champ longueur</param>
		/// <param name="offset">offset dans ce tableau ou se trouve le champ longueur</param>
		/// <returns>la longueur du champ "longueur"</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SizeOfLength(byte[] src, int offset)
		{
			Contract.Debug.Requires(src != null & offset >= 0);
			int v = src[offset];
			return v < 0x80 ? 1 : ((v & 0x7f) + 1);
		}

		/// <summary>Calcule le nombre d'octets qu'occupera la longueur donn�e une fois s�rialis�e</summary>
		/// <param name="length">longueur dont on veut calculer la longueur</param>
		/// <returns>longueur de cette longueur</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SizeOfLength (int length)
		{
			return (length < 0x80) ? 1 : (length < 0x100) ? 2 : (length < 0x10000) ? 3 : (length < 0x1000000) ? 4 : 5;
		}

		/// <summary>Ecrit un champ longueur dans un byte[] selon la norme ASN.1</summary>
		/// <param name="dst">le tableau destination</param>
		/// <param name="pos">offset dans ce tableau o� commencera l'�criture</param>
		/// <param name="length">longueur du champ longueur a �crire</param>
		/// <returns>position dans le tableau apres �criture</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int PutLength(byte[] dst, int pos, int length)
		{
			if (length < 0x80)
			{ // 1-byte
				dst[pos] = (byte) length;
				return pos + 1;
			}
			return PutLengthSlow(dst, pos, length);
		}

		[Pure]
		private static int PutLengthSlow(byte[] dst, int pos, int length)
		{
			//note: 1-byte d�j� g�r�!

			if (length < 0x100)
			{ // 2-byte (pr�fix� par 0x81)
				dst[pos] = 0x81;
				dst[pos + 1] = (byte)length;
				return pos + 2;
			}

			if (length < 0x10000)
			{ // 3-byte (pr�fix� par 0x82)
				dst[pos] = 0x82;
				dst[pos + 1] = (byte)(length >> 8);
				dst[pos + 2] = (byte) length;
				return pos + 3;
			}

			if (length < 0x1000000)
			{ // 4-byte (pr�fix� par 0x83)
				dst[pos] = 0x83;
				dst[pos + 1] = (byte)(length >> 16);
				dst[pos + 2] = (byte)(length >> 8);
				dst[pos + 3] = (byte)length;
				return pos + 4;
			}

			// 5-byte (pr�fix� par 0x83)
			dst[pos] = 0x84;
			dst[pos + 1] = (byte)(length >> 24);
			dst[pos + 2] = (byte)(length >> 16);
			dst[pos + 3] = (byte)(length >> 8);
			dst[pos + 4] = (byte)length;
			return pos + 5;
		}

		/// <summary>Lit un champ longueur depuis un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="pos">offset o� l'on trouve le champ a lire</param>
		/// <returns>la longueur lue</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetLength(byte[] src, int pos)
		{
			Contract.Debug.Requires(src != null & pos >= 0 & pos < src.Length);
			int v;
			return (v = src[pos]) < 0x80 ? v : GetLengthSlow(src, pos);
		}

		[Pure]
		private static int GetLengthSlow(byte[] src, int pos)
		{
			// note: on sait d�j� que le premier byte est >= 0x80
			int len = src[pos++] & 0x7f;
			// note: la valeur est en Big Endian
			switch (len)
			{
				case 1: return src[pos];
				case 2: return (src[pos] << 8) | src[pos + 1];
				case 3: return (src[pos] << 16) | (src[pos + 1] << 8) | src[pos + 2];
				case 4: return (src[pos] << 24) | (src[pos + 1] << 16) | (src[pos + 2] << 8) | src[pos + 3];
				default:
				{ // cas rare, on accumule octet par octet
					int res = 0;
					while (len-- > 0)
					{
						res <<= 8;
						res |= src[pos++];
					}
					return res;
				}
			}
		}

		public static int ReadTagInt32(ref SliceReader reader, string label)
		{
			int len = ReadTagLength(ref reader, label);
			var chunk = reader.ReadBytes(len);
			return GetInt32Data(chunk.Array, chunk.Offset, chunk.Count);
		}

		/// <summary>Lit un champ longueur depuis un byte[] et avance le curseur</summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ReadTagLength(ref SliceReader reader, string label)
		{
			byte val = reader.ReadByte();
			return val < 0x80 ? val : ReadLengthSlow(ref reader, val);
		}

		[Pure]
		private static int ReadLengthSlow(ref SliceReader reader, byte first)
		{
			// note: on sait d�j� que le premier byte est >= 0x80
			int len = first & 0x7f;
			// note: la valeur est en Big Endian
			switch (len)
			{
				case 1: return reader.ReadByte();
				case 2: return reader.ReadFixed16BE();
				case 3: return (int) reader.ReadFixed24BE();
				case 4: return (int) reader.ReadFixed32BE();
				default:
				{ // cas rare, on accumule octet par octet
					int res = 0;
					while (len-- > 0)
					{
						res <<= 8;
						res |= reader.ReadByte();
					}
					return res;
				}
			}
		}

		#endregion

		#region OID Fragments...

		/// <summary>Calcule la longueur d'un fragment d'oid donne</summary>
		/// <param name="value">fragment d'oid dont on veut determiner la longueur</param>
		/// <returns>longueur de ce fragment d'oid</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SizeOfOidFragment(long value)
		{
			return (value < 0x80L) ? 1 : SizeOfOidFragmentSlow(value);
		}

		private static int SizeOfOidFragmentSlow(long value)
		{
			//note: on sait d�j� que ce n'est pas < 0x80
			return (value < 0x4000L) ? 2
				: (value < 0x200000L) ? 3
				: (value < 0x10000000L) ? 4
				: (value < 0x800000000L) ? 5
				: (value < 0x40000000000L) ? 6
				: (value < 0x2000000000000L) ? 7
				: (value < 0x100000000000000L) ? 8
				: 9;
		}

		/// <summary>Ecrit un fragment d'oid dans un byte[] selon la norme ASN.1</summary>
		/// <param name="dst">tableau de destination</param>
		/// <param name="pos">offset auquel on devra placer le fragment d'oid</param>
		/// <param name="value">le fragment d'oid a �crire</param>
		/// <returns>position dans le tableau apr�s �criture</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int PutOidFragment(byte[] dst, int pos, long value)
		{
			if (value < 0x80 && dst != null && (uint)pos < dst.Length)
			{
				dst[pos] = (byte) value;
				return pos + 1;
			}
			return PutOidFragmentSlow(dst, pos, value);
		}

		private static unsafe int PutOidFragmentSlow(byte[] dst, int pos, long value)
		{
			Contract.NotNull(dst);
			Contract.Positive(pos);
			int length = SizeOfOidFragment(value);
			if (length <= 0) throw new InvalidOperationException("Calculated fragment offset and size must be greater than zero");
			if (pos + length > dst.Length) throw new InvalidOperationException("Buffer is too small for oid size");

			fixed (byte* buf = &dst[pos])
			{
				int n = length - 1;
				byte* ptr = buf + n; // dernier octet du fragment
				*ptr-- = (byte)(value & 0x7f);
				while (n-- > 0)
				{
					value >>= 7;
					Paranoid.Assert(ptr >= buf);
					*ptr-- = (byte)(0x80 + (value & 0x7f));
				}
			}
			return pos + length;
		}

		/// <summary>Lit un fragment d'oid a partir d'un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="pos">offset auquel on peur trouver le fragment d'oid a lire</param>
		/// <param name="newpos">re�oit la nouvelle position apr�s lecture</param>
		/// <returns>le fragment d'oid lu</returns>
		public static unsafe long GetNextOidFragment(byte[] src, int pos, out int newpos)
		{
			Contract.NotNull(src);
			Contract.Between(pos, 0, src.Length - 1);

			newpos = pos;
			fixed (byte* buf = &src[0])
			{
				newpos = (int) (ReadNextOidFragment(buf + pos, buf + src.Length, out long res) - buf);
				// par s�curit�, on check si on ne fait pas de conneries
				// => il vaut mieux balancer une exception (qui sera catch�e) que d'�craser la m�moire et crasher le process!
				if (newpos <= pos) throw new InvalidOperationException("Oid fragment parsing error: cursor did not advance");
				if (newpos > src.Length) throw new InvalidOperationException("Oid fragment parsing error: cursor overshot end of buffer");
				return res;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe byte* ReadNextOidFragment(byte* ptr, byte* stop, out long value)
		{
			// la majorit� des OID sont < 0x80, donc on fait en sorte que ce soit inlin�
			return ptr < stop && (value = *ptr) < 0x80 ? ptr + 1 : ReadNextOidFragmentSlow(ptr, stop, out value);
		}

		/// <summary>Lit le prochain fragment d'oid, ou -1 si fin de buffer</summary>
		[Pure]
		private static unsafe byte* ReadNextOidFragmentSlow(byte* ptr, byte* stop, out long value)
		{
			Contract.Debug.Requires(ptr != null & stop != null);
			if (ptr > stop) throw new InvalidOperationException("Oid fragment parsing error: cursor is after the end of the buffer");

			if (ptr == stop)
			{ // end of buffer
				value = -1;
				return ptr;
			}

			// note: on sait d�j� que le premier token est >= 0x80
			value = (*ptr++ & 0x7F) << 7;

			int next = 0xFF;
			while (ptr < stop && (next = *ptr++) >= 0x80)
			{ // tant que le MSB est set
				value += next & 0x7f;
				value <<= 7;
			}
			if (next > 0x7F) throw new InvalidOperationException("Oid fragment parsing error: oid fragment seems to be truncated");

			value += next;
			return ptr;
		}

		[Pure]
		public static long GetLastOidFragment(byte[] buffer, int offset, int count)
		{
			Contract.Debug.Requires(buffer != null && offset >= 0 && count >= 0);

			if (count == 0) return 0; //REVIEW: or bug?

			unsafe
			{
				fixed (byte* bytes = &buffer[offset])
				{
					return GetLastOidFragment(bytes, (uint) count);
				}
			}
		}

		/// <summary>Retourne la valeur du dernier fragment, o� -1 si vide</summary>
		/// <example>"1.3.6.123.4" => 4</example>
		public static unsafe long GetLastOidFragment(byte* buffer, uint count)
		{
			Contract.Debug.Requires(buffer != null);

			if (count == 0) return 0; //REVIEW: or bug?

			// cas sp�cial
			if (count == 1)
			{ // formes courtes: "1.2", "1.3", ...
				int tok = buffer[0];
				if (tok == 0) return -1; // empty
				// ex: 0x2B => "1.3" => 3
				return tok % 40; 
			}

			byte* stop = buffer + count;
			byte* ptr = buffer;
			long frag;
			do
			{
				ptr = ReadNextOidFragment(ptr, stop, out frag);
			}
			while (ptr < stop);

			return frag;
		}

		/// <summary>Retourne la valeur de l'avant dernier fragment, o� -1 si vide (ou taille 1)</summary>
		/// <example>"1.3.6.123.4" => 123</example>
		public static unsafe long GetNextToLastOidFragment(byte* buffer, uint count)
		{
			Contract.Debug.Requires(buffer != null);

			if (count == 0) return 0; //REVIEW: or bug?

			int tok = buffer[0];

			// cas sp�cial
			if (count == 1)
			{ // formes courtes: "1.2", "1.3", ...
				if (tok == 0) return -1; // empty
				// ex: 0x2B => "1.3" => 1
				return tok / 40;
			}

			byte* stop = buffer + count;
			byte* ptr = buffer;
			long nextToLast;
			long current = tok % 40;
			do
			{
				nextToLast = current;
				ptr = ReadNextOidFragment(ptr, stop, out current);
			}
			while (ptr < stop);

			return nextToLast;
		}

		#endregion

		#region UInt64...

		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ulong GetUInt64Data(Slice data)
		{
			return GetUInt64Data(data.Array, data.Offset, data.Count);
		}

		/// <summary>Lit un ulong depuis un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="offset">offset dans ce tableau</param>
		/// <param name="count">longueur en octets de la donn�e a lire</param>
		/// <returns>la donn�e lue</returns>
		[Pure]
		public static ulong GetUInt64Data(byte[] src, int offset, int count)
		{
			Contract.NotNull(src);
			if (count == 0) return 0;
			if (offset < 0 || count < 0 || offset >= src.Length) throw new ArgumentOutOfRangeException(nameof(offset));

			// note: on ne peut pas utiliser BitConvert.ToUInt64 car la taille est variable (0 a 8 bytes)

			ulong res = 0;
			int n = Math.Min(count, 8);
			if (offset + n > src.Length) n = src.Length - offset;

			while (n-- > 0)
			{
				res = (res << 8) | src[offset++];
			}

			return res;
		}

		#endregion

		#region Int32...

		/// <summary>Lit un int32 (sign�) depuis un buffer</summary>
		/// <param name="data">buffer source</param>
		/// <returns>la donn�e lue</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetInt32Data(Slice data)
		{
			return GetInt32Data(data.Array, data.Offset, data.Count);
		}

		/// <summary>Lit un int (signe) depuis un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="offset">offset dans ce tableau</param>
		/// <param name="count">longueur en octets de la donn�e a lire</param>
		/// <returns>la donn�e lue</returns>
		[Pure]
		public static int GetInt32Data(byte[] src, int offset, int count)
		{
			Contract.NotNull(src);
			if (count == 0) return 0;
			if (offset < 0 || count < 0 || offset >= src.Length) throw new ArgumentOutOfRangeException(nameof(offset));

			//TODO: optimize this!

			//l'octet de poids le plus fort indique le signe de notre entier
			uint res = 0;
			uint mul = 1;
			int first = src[offset];

			// shortcut ?
			if (count == 1 && first < 0x80) return first; // 1 seul octet positif

			int n = Math.Min(count, 4);
			if (offset + n > src.Length) n = src.Length - offset;

			while (n-- > 0)
			{
				res = (res << 8) | src[offset++];
				mul <<= 8;
			}
			// r�tablit le signe si besoin
			if (first >= 0x80) res = res - mul;
			return (int)res;
		}

		/// <summary>Calcule la longueur d'un entier donne</summary>
		/// <param name="value">entier dont on veut determiner la longueur</param>
		/// <returns>longueur de cette value selon ASN.1</returns>
		public static int SizeOfInt32Data(int value)
		{
			if ((value < 128) && (value > -128))
				return 1;

			int res = 4;
			while ((((value & 0xFF800000) == 0) || ((value & 0xFF800000) == 0xFF800000)) && (res > 1))
			{
				--res;
				value <<= 8;
			}
			return res;
		}

		/// <summary>PutIntData</summary>
		/// <param name="dst">tableau de destination</param>
		/// <param name="offset">offset a partir duquel commencer l'�criture</param>
		/// <param name="value">int a �crire</param>
		/// <returns>position dans le tableau apr�s �criture</returns>
		public static int PutInt32Data(byte[] dst, int offset, int value)
		{
			Contract.Debug.Requires(dst != null && (uint) offset < dst.Length);
			if ((value < 128) && (value > -128))
			{
				dst[offset + 0] = 0x01;
				dst[offset + 1] = (byte) value;
				return offset + 2;
			}
			return PutInt32DataSlow(dst, offset, value);
		}

		private static int PutInt32DataSlow(byte[] dst, int pos, int src)
		{
			//note: on sait d�j� que src n'est pas entre -128 et +128
			int dataSize = 4;
			while ((((src & 0xFF800000) == 0) || ((src & 0xFF800000) == 0xFF800000)) && (dataSize > 1))
			{
				--dataSize;
				src <<= 8;
			}

			pos = PutLength(dst, pos, dataSize);
			int res = dataSize + pos;
			while ((dataSize--) > 0)
			{
				dst[pos++] = (byte)((src & 0xFF000000) >> 24);
				src <<= 8;
			}

			return res;
		}

		#endregion

		#region Int64...

		/// <summary>Lit un int64 (sign�) depuis un buffer</summary>
		/// <param name="data">buffer source</param>
		/// <returns>la donn�e lue</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long GetInt64Data(Slice data)
		{
			return GetInt64Data(data.Array, data.Offset, data.Count);
		}

		/// <summary>Lit un int64 (sign�) depuis un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="offset">offset dans ce tableau</param>
		/// <param name="count">longueur en octets de la donn�e a lire</param>
		/// <returns>la donn�e lue</returns>
		[Pure]
		public static long GetInt64Data(byte[] src, int offset, int count)
		{
			Contract.NotNull(src);
			if (count == 0) return 0;
			if (offset < 0 || count < 0 || offset >= src.Length) throw new ArgumentOutOfRangeException(nameof(offset));

			//TODO: optimize this!

			//l'octet de poids le plus fort indique le signe de notre entier
			ulong res = 0;
			ulong mul = 1;
			int first = src[offset];

			// shortcut ?
			if (count == 1 && first < 0x80) return first; // 1 seul octet positif

			int n = Math.Min(count, 8);
			if (offset + n > src.Length) n = src.Length - offset;

			while (n-- > 0)
			{
				res = (res << 8) | src[offset++];
				mul <<= 8;
			}
			// r�tabli le signe si besoin
			if (first >= 0x80) res = res - mul;
			return (long) res;
		}

		#endregion

		#region String...

		/// <summary>Lit une cha�ne depuis un byte[]</summary>
		/// <param name="src">tableau source</param>
		/// <param name="offset">offset dans ce tableau</param>
		/// <param name="count">longueur en octets de la donn�e a lire</param>
		/// <returns>la donn�e lue</returns>
		public static string? GetStringData(byte[] src, int offset, int count)
		{
			Contract.Debug.Requires(src != null && offset >= 0 && count >= 0);
			if (count == 0) return null;

			return RawToString(src, offset, count).Trim('\0');
		}

		public static string RawToString(byte[] raw, int offset, int len)
		{
			//TODO: copy de EncodingHelper.RawToString qui �tait marqu�e obsol�te...
			
			Contract.NotNull(raw);
			int n = raw.Length;
			if (offset < 0 || offset >= n) throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset is not within the buffer!");
			if (offset + len > n) throw new ArgumentOutOfRangeException(nameof(len), len, "offset + len exceeds buffer size!");
			unsafe
			{
				char* buffer = stackalloc char[len];
				char* pbuf = buffer;
				n = len;
				fixed (byte* fraw = raw)
				{
					byte* praw = fraw + offset;
					while (n-- > 0) *pbuf++ = (char) (*praw++);
				}
				return new string(buffer, 0, len);
			}
		}


		public static int SizeOfStringData(string value)
		{
			if (string.IsNullOrEmpty(value)) return 1;
			int sz = Encoding.Default.GetByteCount(value);
			return sz + SizeOfLength(sz);
		}

		/// <summary>Ecrit une cha�ne dans un byte[] selon la norme ASN.1</summary>
		/// <param name="dst">tableau de destination</param>
		/// <param name="offset">offset a partir duquel commencer l'�criture</param>
		/// <param name="value">cha�ne � �crire</param>
		/// <returns>position dans le tableau apr�s �criture</returns>
		public static int PutStringData(byte[] dst, int offset, string value)
		{
			Contract.Debug.Requires(dst != null & offset >= 0);
			byte[] src = Encoding.Default.GetBytes(value); // BUGBUG: Encoding.Default d�pend de la culture de l'OS !
			offset = PutLength(dst, offset, src.Length);
			Buffer.BlockCopy(src, 0, dst, offset, src.Length);
			offset += src.Length;
			return offset;
		}

		#endregion
	}
}
