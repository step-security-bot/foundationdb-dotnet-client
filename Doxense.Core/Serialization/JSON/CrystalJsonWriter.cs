﻿#region Copyright (c) 2023-2024 SnowBank SAS, (c) 2005-2023 Doxense SAS
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 	* Redistributions of source code must retain the above copyright
// 	  notice, this list of conditions and the following disclaimer.
// 	* Redistributions in binary form must reproduce the above copyright
// 	  notice, this list of conditions and the following disclaimer in the
// 	  documentation and/or other materials provided with the distribution.
// 	* Neither the name of SnowBank nor the
// 	  names of its contributors may be used to endorse or promote products
// 	  derived from this software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL SNOWBANK SAS BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

//#define DEBUG_JSON_SERIALIZER

namespace Doxense.Serialization.Json
{
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
	using System.Globalization;
	using System.IO;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using System.Text;
	using System.Threading;

	using Doxense.IO;
	using Doxense.Linq;
	using NodaTime;
	using NodaTime.Text;

	/// <summary>Serialize values into JSON</summary>
	[DebuggerDisplay("Json={!m_javascript}, Formatted={m_formatted}, Depth={m_objectGraphDepth}")]
	[PublicAPI]
	[DebuggerNonUserCode]
	public sealed class CrystalJsonWriter : IDisposable
	{
		private const int MaximumObjectGraphDepth = 16;

		public enum NodeType
		{
			TopLevel = 0,
			Object,
			Array,
			Property
		}

		[DebuggerDisplay("Node={Node}, Tail={Tail}")]
		public struct State
		{
			/// <summary>False si c'est le premier "élément" (d'un objet ou d'une array)</summary>
			internal bool Tail;
			/// <summary>Type de node actuel</summary>
			internal NodeType Node;
			/// <summary>Valeur de l'indentation actuelle</summary>
			internal string Indentation;
		}

		// Settings
		private ValueStringWriter m_buffer;
		private State m_state;

		private TextWriter? m_output;
		private int m_autoFlush;

		private bool m_javascript;
		private bool m_formatted;
		private bool m_indented;
		private CrystalJsonSettings.DateFormat m_dateFormat;
		private CrystalJsonSettings.FloatFormat m_floatFormat;
		private bool m_discardDefaults;
		private bool m_discardNulls;
		private bool m_discardClass;
		private bool m_markVisited;
		private bool m_camelCase;
		private bool m_enumAsString;
		private bool m_enumCamelCased;
		private CrystalJsonSettings m_settings;
		private ICrystalJsonTypeResolver m_resolver;
		private JsonPropertyAttribute? m_attributes;
		private object[]? m_visitedObjects;
		private int m_visitedCursor;
		private int m_objectGraphDepth;

		public CrystalJsonWriter(TextWriter output, int autoFlush, CrystalJsonSettings? settings, ICrystalJsonTypeResolver? resolver)
		{
			Contract.NotNull(output);
			Initialize(0, settings, resolver);
			m_output = output;
			m_autoFlush = autoFlush > 0 ? autoFlush : 64 * 1024;
		}

		public CrystalJsonWriter(int initialCapacity, CrystalJsonSettings? settings, ICrystalJsonTypeResolver? resolver)
		{
			Initialize(initialCapacity, settings, resolver);
		}

		internal CrystalJsonWriter() { }

		internal ref ValueStringWriter Buffer => ref m_buffer;

		public TextWriter? Output => m_output;

		public CrystalJsonSettings Settings => m_settings;

		public ICrystalJsonTypeResolver Resolver => m_resolver;

		/// <summary>Specifies if we are targeting JavaScript, instead of JSON</summary>
		/// <remarks>If <see langword="true"/>, all strings will be escaped using single quotes (<c>'</c>), and property names will only be quoted if necessary</remarks>
		public bool JavaScript => m_javascript;

		/// <summary>Specifies if we will discard value type members that have a default value (0, false, null for Nullable&lt;T&;gt;, ...)</summary>
		public bool DiscardDefaults => m_discardDefaults;

		/// <summary>Specified if we wil discard reference type members that are null</summary>
		public bool DiscardNulls => m_discardNulls;

		/// <summary>Specifies if we wil discard the "_class" attribute</summary>
		public bool DiscardClass => m_discardClass;

		/// <summary>Format used to convert dates</summary>
		public CrystalJsonSettings.DateFormat DateFormatting => m_dateFormat;

		/// <summary>Format used to convert floating point numbers</summary>
		public CrystalJsonSettings.FloatFormat FloatFormatting => m_floatFormat;

		/// <summary>Current depth when serializing (0 for top level)</summary>
		public int Depth => m_objectGraphDepth;

		/// <summary>Specifies whether the writer will automatically indent all values (to enhance readability by humans)</summary>
		public bool Indented => m_indented;

		/// <summary>Specifies whether the writer will insert spaces between tokens (to enhance readability by humans)</summary>
		public bool Formatted => m_formatted;

		public void Dispose()
		{
			if (m_output != null)
			{
				FlushBuffer();
				m_output = null;
			}

			m_visitedObjects?.AsSpan().Clear();
		}

		[MemberNotNull([ nameof(m_settings), nameof(m_resolver), ])]
		internal void Initialize(int initialCapacity, CrystalJsonSettings? settings, ICrystalJsonTypeResolver? resolver)
		{
			settings ??= CrystalJsonSettings.Json;
			resolver ??= CrystalJson.DefaultResolver;

			if (!ReferenceEquals(settings, m_settings) || ReferenceEquals(resolver, m_resolver))
			{
				m_javascript = settings.TargetLanguage == CrystalJsonSettings.Target.JavaScript;
				m_formatted = settings.TextLayout != CrystalJsonSettings.Layout.Compact;
				m_indented = settings.TextLayout == CrystalJsonSettings.Layout.Indented;
				m_state.Indentation = string.Empty;
				m_dateFormat = settings.DateFormatting != CrystalJsonSettings.DateFormat.Default ? settings.DateFormatting : (m_javascript ? CrystalJsonSettings.DateFormat.JavaScript : CrystalJsonSettings.DateFormat.TimeStampIso8601);
				m_floatFormat = settings.FloatFormatting != CrystalJsonSettings.FloatFormat.Default ? settings.FloatFormatting : (m_javascript ? CrystalJsonSettings.FloatFormat.JavaScript : CrystalJsonSettings.FloatFormat.Symbol);
				m_discardDefaults = settings.HideDefaultValues;
				m_discardNulls = m_discardDefaults || !settings.ShowNullMembers;
				m_discardClass = settings.HideClassId;
				m_camelCase = settings.UseCamelCasingForNames;
				m_enumAsString = settings.EnumsAsString;
				m_enumCamelCased = settings.UseCamelCasingForEnums;
				m_markVisited = !settings.DoNotTrackVisitedObjects;
			}

			m_buffer = new ValueStringWriter(initialCapacity != 0 ? initialCapacity : (settings.OptimizeForLargeData ? 64 * 1024 : 1024));
			m_settings = settings;
			m_resolver = resolver;
			m_output = null;
			m_autoFlush = 0;
		}

		public void Initialize(TextWriter output, int autoFlush, CrystalJsonSettings settings, ICrystalJsonTypeResolver resolver)
		{
			Contract.NotNull(output);
			Contract.Positive(autoFlush);

			Initialize(0, settings, resolver);
			m_output = output;
			m_autoFlush = autoFlush > 0 ? autoFlush : 64 * 1024;
		}

		/// <summary>Returns the JSON text written so far</summary>
		/// <returns>JSON text</returns>
		/// <exception cref="InvalidOperationException">If this instance is outputing to a TextWriter</exception>
		public string GetString()
		{
			return m_output == null ? m_buffer.ToString() : throw new InvalidOperationException("This method cannot be used when writing to a TextWriter");
		}

		/// <summary>Returns the JSON text written, and clear the writer</summary>
		/// <returns>JSON text</returns>
		/// <exception cref="InvalidOperationException">If this instance is outputing to a TextWriter</exception>
		public string GetStringAndClear()
		{
			return m_output == null ? m_buffer.ToStringAndClear() : throw new InvalidOperationException("This method cannot be used when writing to a TextWriter");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void MaybeFlush()
		{
			if (m_autoFlush > 0 && m_buffer.Count >= m_autoFlush)
			{
				FlushBuffer();
			}
		}

		internal void FlushBuffer()
		{
			if (m_output != null)
			{
				WriteBufferToOutput();
			}
			m_buffer.Clear();

			[MethodImpl(MethodImplOptions.NoInlining)]
			void WriteBufferToOutput()
			{
				Contract.Debug.Assert(m_output != null);

				//note: if the TextWriter implementation does not overload Write(ReadOnlySpan<char>),
				// the base implementation simply uses a tempt buffer from ArrayPool<char>.Shared, and calls Write(char[], int, int)
				// => we expect most of the callers to use either FastStringWriter, StringWriter or StreamWriter, so we are "ok" with that
				m_output.Write(m_buffer.Span);

				m_buffer.Clear();
			}
		}

		internal Task FlushBufferAsync(CancellationToken ct)
		{
			if (m_output != null)
			{
				return WriteBufferToOutputAsync(ct);
			}

			m_buffer.Clear();
			return Task.CompletedTask;

			[MethodImpl(MethodImplOptions.NoInlining)]
			async Task WriteBufferToOutputAsync(CancellationToken cancel)
			{
				//note: if the TextWriter implementation does not overload WriteAsync(ReadOnlyMemory<char>),
				// the base implementation simply Task.Factory.StartNew((...) => output.Write(ReadOnlySpan<char>))
				// also, the CancellationToken is only check _BEFORE_ the write, but the write itself is not cancellable :(
				await m_output.WriteAsync(m_buffer.Memory, cancel).ConfigureAwait(false);

				m_buffer.Clear();
			}
		}


		public void Flush()
		{
			if (m_output != null)
			{
				FlushBuffer();
				m_output.Flush();
			}
		}

		public async Task FlushAsync(CancellationToken ct)
		{
			if (m_output != null)
			{
				// first flush the buffer content into the writer
				await FlushBufferAsync(ct).ConfigureAwait(false);

				// and then flush the writer itself (into its base stream)
#if NET8_0_OR_GREATER
				await m_output.FlushAsync(ct).ConfigureAwait(false);
#else
				//BUGBUG: .NET 6 does not have an overload that takes a cancellation token :(
				ct.ThrowIfCancellationRequested();
				await m_output.FlushAsync();
#endif
			}
		}

		/// <summary>Apply casing policy to a property name</summary>
		/// <param name="name">Name (ex: "FooBar")</param>
		/// <returns>Same name, or camel cased version (ex: "FooBar" => "fooBar" if Camel Casing is selected)</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal string FormatName(string name)
		{
			return m_camelCase ? CamelCase(name) : name;
		}

		internal static string CamelCase(string name)
		{
			// if the first character is already lowercase, we can skip it
			char first = name[0];
			if (first is '_' or (>= 'a' and <= 'z'))
			{
				return name;
			}

			// lower case the first character
			var chars = name.ToCharArray();
			chars[0] = char.ToLowerInvariant(first);
			return new string(chars);
		}

		/// <summary>Write the "_class" attribute with the resolved type id</summary>
		public void WriteClassId(Type type)
		{
			var typeDef = this.Resolver.ResolveJsonType(type);
			if (typeDef == null) throw CrystalJson.Errors.Serialization_CouldNotResolveTypeDefinition(type);
			WriteField(JsonTokens.CustomClassAttribute, typeDef.ClassId);
		}

		/// <summary>Write the "_class" attribute with the specified type id</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteClassId(string classId)
		{
			WriteField(JsonTokens.CustomClassAttribute, classId);
		}

		/// <summary>Write a comment</summary>
		/// <remarks>Not all JSON parser will accept comments! Only use when you know that all parsers that will consume this understand and allow comments!</remarks>
		public void WriteComment(string comment)
		{
			m_buffer.Write("/* ", comment.Replace("*/", "* /"), " */");
		}

		/// <summary>Write the "null" literal</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteNull()
		{
			m_buffer.Write(JsonTokens.Null);
		}

		/// <summary>Write the empty object "{}" literal</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEmptyObject()
		{
			m_buffer.Write(m_formatted ? JsonTokens.EmptyObjectFormatted : JsonTokens.EmptyObjectCompact);
		}

		/// <summary>Write the empty array "[]" literal</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEmptyArray()
		{
			m_buffer.Write(m_formatted ? JsonTokens.EmptyArrayFormatted : JsonTokens.EmptyArrayCompact);
		}

		/// <summary>Write a coma separator (",") between two fields, unless this is the first element of an array</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteFieldSeparator()
		{
			if (m_indented)
			{
				m_buffer.Write(
					m_state.Tail ? JsonTokens.CommaIndented : JsonTokens.NewLine,
					m_state.Indentation
				);
			}
			else if (m_formatted)
			{
				m_buffer.Write(m_state.Tail ? JsonTokens.CommaFormatted : " ");
			}
			else if (m_state.Tail)
			{
				m_buffer.Write(',');
			}
			m_state.Tail = true;
		}

		/// <summary>Properly indent the first element of an array</summary>
		public void WriteHeadSeparator()
		{
			Contract.Debug.Requires(!m_state.Tail);
			m_state.Tail = true;
			if (m_indented)
			{
				m_buffer.Write(
					JsonTokens.NewLine,
					m_state.Indentation
				);
			}
			else if (m_formatted)
			{
				m_buffer.Write(' ');
			}
		}

		/// <summary>Write a coma between elements of an array</summary>
		public void WriteTailSeparator()
		{
			Contract.Debug.Requires(m_state.Tail);
			if (m_indented)
			{
				m_buffer.Write(
					JsonTokens.CommaIndented,
					m_state.Indentation
				);
			}
			else if (m_formatted)
			{
				m_buffer.Write(JsonTokens.CommaFormatted);
			}
			else
			{
				m_buffer.Write(',');
			}
		}

		/// <summary>Write a coma between elements of an inline array, unless this is the first element</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteInlineFieldSeparator()
		{
			if (m_state.Tail)
			{
				WriteInlineTailSeparator();
			}
			else
			{
				WriteInlineHeadSeparator();
			}
		}

		public void WriteInlineHeadSeparator()
		{
			Contract.Debug.Requires(!m_state.Tail);
			m_state.Tail = true;
			if (m_indented | m_formatted)
			{
				m_buffer.Write(' ');
			}
		}

		public void WriteInlineTailSeparator()
		{
			Contract.Debug.Requires(m_state.Tail);
			if (m_indented | m_formatted)
			{
				m_buffer.Write(JsonTokens.CommaFormatted);
			}
			else
			{
				m_buffer.Write(',');
			}
		}

		public JsonPropertyAttribute? PushAttributes(JsonPropertyAttribute attributes)
		{
			var tmp = m_attributes;
			m_attributes = attributes;
			return tmp;
		}

		public void PopAttributes(JsonPropertyAttribute? attributes)
		{
			m_attributes = attributes;
		}

		/// <summary>Push a new state onto the stack</summary>
		/// <param name="type">Type of the new node (Object, Array, ...)</param>
		/// <returns>Previous state</returns>
		/// <remarks>The "stack" itself is handled by the caller's own stack. The previous state should be stored in a local variable, and passed back to <see cref="PopState"/> once the array of object is completed.</remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal State PushState(NodeType type)
		{
			var state = m_state;
			m_state.Tail = false;
			m_state.Node = type;
			return state;
		}

		/// <summary>Pop and return the state from the stack</summary>
		/// <param name="state">Copy of the previous state (as returned by <see cref="PushState"/>)</param>
		/// <returns>Current state (before the pop)</returns>
		/// <remarks>The "stack" itself is handled by the caller's own stack.</remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal State PopState(State state)
		{
			var tmp = m_state;
			m_state = state;
			return tmp;
		}

		/// <summary>Reset the state of the writer, so that it can be reused to write a new JSON document</summary>
		/// <remarks>
		/// Only use when reusing the same writer in a loop or batch.
		/// The caller must be careful to reset the internal state of the inner TextWriter that is used by this instance!
		/// </remarks>
		public void ResetState()
		{
			//note: we must keep the current indentation mode!
			m_state.Node = NodeType.TopLevel;
			m_state.Tail = false;
		}

		/// <summary>Return a copy of the current state</summary>
		internal State CurrentState
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => m_state;
		}

		/// <summary>Mark the start of a new item in an array, or field in an object</summary>
		/// <returns><see langword="false"/> if this is the first element of the current state, or <see langword="true"/> if there was at least one element written before.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool MarkNext()
		{
			bool tail = m_state.Tail;
			m_state.Tail = true;
			return tail;
		}

		/// <summary>Start a new JSON object</summary>
		/// <returns>Previous state, that should be passed to the corresponding <see cref="EndObject"/></returns>
		public State BeginObject()
		{
			var state = m_state;
			m_buffer.Write('{');
			m_state.Tail = false;
			m_state.Node = NodeType.Object;
			if (m_indented) m_state.Indentation += '\t';
			return state;
		}

		/// <summary>End a JSON object</summary>
		/// <param name="state">Value that was returned by the call to <see cref="BeginObject"/> for this object</param>
		/// <remarks>The caller should store the state in a local variable. Mixing states between objects and arrays will CORRUPT the resulting JSON document!</remarks>
		public void EndObject(State state)
		{
			Paranoid.Requires(m_state.Node == NodeType.Object);
			if (m_indented)
			{
				m_buffer.Write(
					JsonTokens.NewLine,
					state.Indentation,
					'}'
				);
			}
			else if (m_formatted)
			{
				m_buffer.Write(JsonTokens.CurlyCloseFormatted);
			}
			else
			{
				m_buffer.Write('}');
			}
			m_state = state;

			MaybeFlush();
		}

		/// <summary>Start a new JSON array</summary>
		/// <returns>Previous state, that should be passed to the corresponding <see cref="EndArray"/></returns>
		public State BeginArray()
		{
			var state = m_state;
			m_buffer.Write('[');
			m_state.Tail = false;
			m_state.Node = NodeType.Array;
			if (m_indented) m_state.Indentation += '\t';
			return state;
		}

		/// <summary>Start a new JSON inline array</summary>
		/// <returns>Previous state, that should be passed to the corresponding <see cref="EndArray"/></returns>
		/// <remarks>Inline arrays will attempt to keep all elements on a single line. Use this when serialing "vector" or "tuples" that are not techincally an array, but are expressed as an array (the XYZ coordinates of a point, a key/value pair, ...)</remarks>
		public State BeginInlineArray()
		{
			var state = m_state;
			m_buffer.Write('[');
			m_state.Tail = false;
			m_state.Node = NodeType.Array;
			return state;
		}

		/// <summary>End a JSON array</summary>
		/// <param name="state">Value that was returned by the call to <see cref="BeginArray"/> for this array</param>
		/// <remarks>The caller should store the state in a local variable. Mixing states between objects and arrays will CORRUPT the resulting JSON document!</remarks>
		public void EndArray(State state)
		{
			Paranoid.Requires(m_state.Node == NodeType.Array);
			if (m_indented)
			{
				m_buffer.Write(
					JsonTokens.NewLine,
					state.Indentation,
					']'
				);
			}
			else if (m_formatted)
			{
				m_buffer.Write(JsonTokens.BracketCloseFormatted); // " ]"
			}
			else
			{
				m_buffer.Write(']');
			}
			m_state = state;

			MaybeFlush();
		}

		/// <summary>End a JSON inline array</summary>
		/// <param name="state">Value that was returned by the call to <see cref="BeginArray"/> for this array</param>
		/// <remarks>The caller should store the state in a local variable. Mixing states between objects and arrays will CORRUPT the resulting JSON document!</remarks>
		public void EndInlineArray(State state)
		{
			Paranoid.Requires(m_state.Node == NodeType.Array);
			if (m_indented | m_formatted)
			{
				m_buffer.Write(JsonTokens.BracketCloseFormatted); // " ]"
			}
			else
			{
				m_buffer.Write(']');
			}
			m_state = state;
			MaybeFlush();
		}

		#region WritePair ...

		public void WritePair(int key, bool value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(int key, int value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(int key, string? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(int key, ReadOnlySpan<char> value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(int key, ReadOnlyMemory<char> value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value.Span);
			EndArray(state);
		}

		public void WritePair(int key, StringBuilder? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(int key, JsonValue? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			(value ?? JsonNull.Missing).JsonSerialize(this);
			EndArray(state);
		}

		public void WritePair<TValue>(int key, TValue value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			VisitValue<TValue>(value);
			EndArray(state);
		}

		public void WritePair(string? key, bool value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(string? key, int value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(string? key, string? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(string? key, ReadOnlySpan<char> value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(string? key, ReadOnlyMemory<char> value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value.Span);
			EndArray(state);
		}

		public void WritePair(string? key, StringBuilder? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			WriteValue(value);
			EndArray(state);
		}

		public void WritePair(string? key, JsonValue? value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			(value ?? JsonNull.Missing).JsonSerialize(this);
			EndArray(state);
		}

		public void WritePair<TValue>(string? key, TValue value)
		{
			var state = BeginArray();
			WriteHeadSeparator();
			WriteValue(key);
			WriteTailSeparator();
			VisitValue<TValue>(value);
			EndArray(state);
		}

		#endregion

		#region WriteInlinePair...

		public void WriteInlinePair(int key, bool value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, int value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, string? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, ReadOnlySpan<char> value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, ReadOnlyMemory<char> value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value.Span);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, StringBuilder? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(int key, JsonValue? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			(value ?? JsonNull.Missing).JsonSerialize(this);
			EndInlineArray(state);
		}

		public void WriteInlinePair<TValue>(int key, TValue value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			VisitValue<TValue>(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, bool value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, int value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, string? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, ReadOnlySpan<char> value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, ReadOnlyMemory<char> value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value.Span);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, JsonValue? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			(value ?? JsonNull.Missing).JsonSerialize(this);
			EndInlineArray(state);
		}

		public void WriteInlinePair<TValue>(string? key, TValue value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			VisitValue<TValue>(value);
			EndInlineArray(state);
		}

		public void WriteInlinePair(string? key, StringBuilder? value)
		{
			var state = BeginInlineArray();
			WriteInlineHeadSeparator();
			WriteValue(key);
			WriteInlineTailSeparator();
			WriteValue(value);
			EndInlineArray(state);
		}

		#endregion

		/// <summary>Mark an instance as already visited, and perform infinite loop detection</summary>
		/// <param name="value">Instance currently being serialized</param>
		/// <exception cref="System.InvalidOperationException">If this instance is already being serialized, meaning that there is a cycle where the object (or one of its children) is referencing back to itself</exception>
		/// <remarks>The caller should call <see cref="Leave"/> once this instance has been handled. Failure to do so will leak memory, and also prevent from serializing the same object multiple times (cached singletons, ...)</remarks>
		public void MarkVisited(object? value)
		{
			if (m_objectGraphDepth >= MaximumObjectGraphDepth)
			{ // protect against very deep object graphs
				throw CrystalJson.Errors.Serialization_FailTooDeep(m_objectGraphDepth, value);
			}
			if (value != null && m_markVisited)
			{ // protect against loops in the object graph that would cause a stack overflow
				if (m_visitedObjects == null)
				{
					m_visitedObjects = new object[4];
					m_visitedCursor = 0;
				}
				else if (AlreadyVisited(m_visitedObjects.AsSpan(0, m_visitedCursor), m_visitedCursor))
				{
					if (!TypeSafeForRecursion(value.GetType()))
					{
						throw CrystalJson.Errors.Serialization_ObjectRecursionIsNotAllowed(m_visitedObjects, value, m_objectGraphDepth);
					}
				}
				PushVisited(ref m_visitedObjects, ref m_visitedCursor, value);
			}
			++m_objectGraphDepth;

			static bool AlreadyVisited(ReadOnlySpan<object> stack, object value)
			{
				foreach (var item in stack)
				{
					if (ReferenceEquals(item, value))
					{
						return true;
					}
				}
				return false;
			}

			static void PushVisited(ref object[] buffer, ref int cursor, object value)
			{
				if (cursor >= buffer.Length)
				{
					Array.Resize(ref buffer, checked(buffer.Length + 4));
				}
				buffer[cursor++] = value;
			}
		}

		internal static bool TypeSafeForRecursion(Type type)
		{
			// known types that are "safe" from any possible loop
			return type.IsValueType || type == typeof(string) || type == typeof(System.Net.IPAddress);
		}

		/// <summary>Mark the current object as completed, and remove it from the loop tracking list</summary>
		/// <param name="value">Same value that was passed to <see cref="MarkVisited"/></param>
		public void Leave(object? value)
		{
			if (m_objectGraphDepth == 0) throw CrystalJson.Errors.Serialization_InternalDepthInconsistent();
			if (value != null && m_markVisited && m_visitedObjects != null && m_visitedCursor > 0)
			{
				var previous = PopVisited(ref m_visitedObjects, ref m_visitedCursor);
				if (!ReferenceEquals(previous, value))
				{
					throw CrystalJson.Errors.Serialization_LeaveNotSameThanMark(m_objectGraphDepth, value);
				}
			}
			--m_objectGraphDepth;

			static object PopVisited(ref object[] buffer, ref int cursor)
			{
				Contract.Debug.Requires(buffer != null && cursor > 0 && cursor <= buffer.Length);
				--cursor;
				var obj = buffer[cursor];
				buffer[cursor] = default!;
				return obj;
			}
		}

		#region Basic Type Serializers...

		/// <summary><b>[CAUTION]</b> Writes a raw JSON literal into the output buffer, without any checks or encoding.</summary>
		/// <param name="rawJson">JSON snippet that is already encoded</param>
		/// <remarks>Danger, Will Robinson !!!" Only use it if you know what you are doing, such as outputing already encoded JSON constants or in very specific use cases where performance superseeds safety!</remarks>
		public void WriteRaw(string? rawJson)
		{
			if (!string.IsNullOrEmpty(rawJson))
			{
				m_buffer.Write(rawJson);
			}
		}

		/// <summary><b>[CAUTION]</b> Writes a raw JSON literal into the output buffer, without any checks or encoding.</summary>
		/// <param name="rawJson">JSON snippet that is already encoded</param>
		/// <remarks>"Danger, Will Robinson !!!" Only use it if you know what you are doing, such as outputing already encoded JSON constants or in very specific use cases where performance superseeds safety!</remarks>
		public void WriteRaw(ref DefaultInterpolatedStringHandler rawJson)
		{
			WriteRaw(rawJson.ToStringAndClear());
		}

		/// <summary>Write a property name that is KNOWN to not require any escaping.</summary>
		/// <param name="name">Name of the property that MUST NOT REQUIRED ANY ESCAPING!</param>
		/// <remarks>Calling this with a .NET object property or field name (obtained via reflection or nameof(...)) is OK, but calling with a dictionary key or user-input is NOT safe!</remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteName(string name)
		{
			WriteFieldSeparator();
			WritePropertyName(name, knownSafe: true);
		}

		/// <summary>Write a property name that is KNOWN to not require any escaping.</summary>
		/// <param name="name">Name of the property that MUST NOT REQUIRED ANY ESCAPING!</param>
		/// <remarks>Calling this with a .NET object property or field name (obtained via reflection or nameof(...)) is OK, but calling with a dictionary key or user-input is NOT safe!</remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteName(in JsonEncodedPropertyName name)
		{
			WriteFieldSeparator();
			WritePropertyName(in name);
		}

		/// <summary>Write a property name that MAY require escaping.</summary>
		/// <param name="name">Name of the property that will be escaped if necessary</param>
		/// <remarks>
		/// <para>This method should be used whenever the origin of key is not controlled, and may contains any character that would required escaping ('\', '"', ...).</para>
		/// </remarks>
		public void WriteNameEscaped(string name)
		{
			WriteFieldSeparator();
			WritePropertyName(name, knownSafe: false);
		}

		/// <summary>Write a property name that MAY require escaping.</summary>
		/// <param name="name">Name of the property that will be escaped if necessary</param>
		/// <remarks>
		/// <para>This method should be used whenever the origin of key is not controlled, and may contains any character that would required escaping ('\', '"', ...).</para>
		/// </remarks>
		public void WriteNameEscaped(ReadOnlySpan<char> name)
		{
			WriteFieldSeparator();
			WritePropertyName(name, knownSafe: false);
		}

		internal void WritePropertyName(string name, bool knownSafe)
		{
			if (!m_javascript)
			{
				string formattedName = FormatName(name);
				if (knownSafe || !JsonEncoding.NeedsEscaping(formattedName))
				{
					m_buffer.Write(
						'"',
						formattedName,
						m_formatted ? JsonTokens.QuoteColonFormatted : JsonTokens.QuoteColonCompact
					);
				}
				else
				{
					CrystalJsonFormatter.WriteJsonStringSlow(ref m_buffer, name);
					m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
				}
			}
			else
			{
				WriteJavaScriptName(name);
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		internal void WriteJavaScriptName(string name)
		{
			m_buffer.Write(
				Doxense.Web.JavaScriptEncoding.EncodePropertyName(FormatName(name)),
				m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact
			);
		}

		internal void WritePropertyName(ReadOnlySpan<char> name, bool knownSafe)
		{
			if (m_javascript)
			{
				WriteJavaScriptName(name);
				return;
			}

			if (knownSafe || !JsonEncoding.NeedsEscaping(name))
			{
				if (!m_camelCase || (name[0] is '_' or (>= 'a' and <= 'z')))
				{
					m_buffer.Write('"', name);
				}
				else
				{
					m_buffer.Write('"', char.ToLowerInvariant(name[0]));
					if (name.Length > 1)
					{
						m_buffer.Write(name[1..]);
					}
				}
				m_buffer.Write(m_formatted ? JsonTokens.QuoteColonFormatted : JsonTokens.QuoteColonCompact);
			}
			else
			{
				CrystalJsonFormatter.WriteJsonStringSlow(ref m_buffer, name);
				m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
			}
		}

		internal void WriteJavaScriptName(ReadOnlySpan<char> name)
		{
			if (!m_camelCase || (name[0] is '_' or (>= 'a' and <= 'z')))
			{
				Doxense.Web.JavaScriptEncoding.EncodePropertyNameTo(ref m_buffer, name);
			}
			else
			{
				//TODO: REVIEW: better way for this?
				Span<char> tmp = stackalloc char[name.Length];
				tmp[0] = char.ToLowerInvariant(name[0]);
				name[1..].CopyTo(tmp[1..]);
				Doxense.Web.JavaScriptEncoding.EncodePropertyNameTo(ref m_buffer, name);
			}
			m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
		}

		/// <summary>Write a field name that is an integer</summary>
		/// <param name="name">Integer</param>
		/// <remarks>This is used for objects with keys that are integers like: <c>{ "0": ..., "1": ...., ....}</c>.</remarks>
		public void WriteName(long name)
		{
			WriteFieldSeparator();
			WritePropertyName(name);
		}

		internal void WritePropertyName(long name)
		{
			if (!m_javascript)
			{
				m_buffer.Write('"');
				WriteValue(name);
				m_buffer.Write(m_formatted ? JsonTokens.QuoteColonFormatted : JsonTokens.QuoteColonCompact);
			}
			else
			{
				WriteValue(name);
				m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
			}
		}

		public void WriteName(int name)
		{
			WriteFieldSeparator();
			WritePropertyName(name);

		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void WritePropertyName(in JsonEncodedPropertyName name)
		{
			m_buffer.Write(
				m_javascript ? name.JavaScriptLiteral : name.JsonLiteral,
				m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact
			);
		}

		internal void WritePropertyName(int name)
		{
			if (!m_javascript)
			{
				m_buffer.Write('"');
				WriteValue(name);
				m_buffer.Write(m_formatted ? JsonTokens.QuoteColonFormatted : JsonTokens.QuoteColonCompact);
			}
			else
			{
				WriteValue(name);
				m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
			}
		}

		public void WriteUnsafeName(string name)
		{
			WriteFieldSeparator();
			if (!m_javascript)
			{
				CrystalJsonFormatter.WriteJsonString(ref m_buffer, name);
			}
			else
			{
				CrystalJsonFormatter.WriteJavaScriptString(ref m_buffer, FormatName(name));
			}
			m_buffer.Write(m_formatted ? JsonTokens.ColonFormatted : JsonTokens.ColonCompact);
		}

		#region WriteValue...

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(JsonValue? value)
		{
			if (value != null)
			{
				value.JsonSerialize(this);
			}
			else
			{
				WriteNull();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(string? value)
		{
			if (!m_javascript)
			{
				CrystalJsonFormatter.WriteJsonString(ref m_buffer, value);
			}
			else
			{
				CrystalJsonFormatter.WriteJavaScriptString(ref m_buffer, value);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(ReadOnlySpan<char> value)
		{
			if (!m_javascript)
			{
				CrystalJsonFormatter.WriteJsonString(ref m_buffer, value);
			}
			else
			{
				CrystalJsonFormatter.WriteJavaScriptString(ref m_buffer, value);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(ReadOnlyMemory<char> value)
		{
			if (!m_javascript)
			{
				CrystalJsonFormatter.WriteJsonString(ref m_buffer, value.Span);
			}
			else
			{
				CrystalJsonFormatter.WriteJavaScriptString(ref m_buffer, value.Span);
			}
		}

		public void WriteValue(char value)
		{
			// replace the NUL character (\0) by 'null'
			if (value == '\0')
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!JsonEncoding.NeedsEscaping(value))
			{
				m_buffer.Write('"', value, '"');
			}
			else
			{
				//TODO: PERF: optimize this!
				m_buffer.Write(JsonEncoding.AppendSlow(new StringBuilder(), new string(value, 1), true).ToString());
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(char? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(StringBuilder? value)
		{
			WriteValue(value?.ToString());
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(bool value)
		{
			m_buffer.Write(value ? JsonTokens.True : JsonTokens.False);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(bool? value)
		{
			m_buffer.Write(value == null ? JsonTokens.Null : value.Value ? JsonTokens.True : JsonTokens.False);
		}

		public void WriteValue(byte value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(byte? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(sbyte value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(sbyte? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(short value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(short? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(ushort value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(ushort? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(int value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(int? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(uint value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(uint? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(long value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(long? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(ulong value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(ulong? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(float value)
		{
			// special case for NaN and +/-Infinity that require specific tokens, depending on the configuration
			if (!float.IsFinite(value))
			{
				m_buffer.Write(
					  float.IsNaN(value) ? CrystalJsonFormatter.GetNaNToken(m_floatFormat)
					: value > 0 ? CrystalJsonFormatter.GetPositiveInfinityToken(m_floatFormat)
					: CrystalJsonFormatter.GetNegativeInfinityToken(m_floatFormat)
				);
			}
			else
			{
				m_buffer.Write(value);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(float? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(double value)
		{
			// special case for NaN and +/-Infinity that require specific tokens, depending on the configuration
			if (!double.IsFinite(value))
			{
				m_buffer.Write(
					  double.IsNaN(value) ? CrystalJsonFormatter.GetNaNToken(m_floatFormat)
					: value > 0 ? CrystalJsonFormatter.GetPositiveInfinityToken(m_floatFormat)
					: CrystalJsonFormatter.GetNegativeInfinityToken(m_floatFormat)
				);
			}
			else
			{
				m_buffer.Write(value);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(double? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEnumInteger<TEnum>(TEnum value)
			where TEnum : struct, System.Enum
		{
			//note: we could cast to int and call WriteInt32(...), but some enums do not derive from Int32 :(
			if (Unsafe.SizeOf<TEnum>() == 4)
			{
				WriteValue((int) (object) value);
			}
			else
			{
				m_buffer.Write(value.ToString("D"));
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEnumInteger(Enum? value)
		{
			if (value == null)
			{
				WriteNull();
				return;
			}

			//note: we could cast to int and call WriteInt32(...), but some enums do not derive from Int32 :(
			m_buffer.Write(value.ToString("D"));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEnumString<TEnum>(TEnum value)
			where TEnum: struct, System.Enum
		{
			string str = value.ToString("G");
			if (m_enumCamelCased) str = CamelCase(str);
			WriteValue(str);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteEnumString(Enum? value)
		{
			if (value == null)
			{
				WriteNull();
				return;
			}

			string str = value.ToString("G");
			if (m_enumCamelCased) str = CamelCase(str);
			WriteValue(str);
		}

		public void WriteEnum<TEnum>(TEnum value)
			where TEnum: struct, System.Enum
		{
			var fmt = m_attributes?.EnumFormat ?? JsonEnumFormat.Inherits;
			if ((fmt == JsonEnumFormat.Inherits && m_enumAsString) || fmt == JsonEnumFormat.String)
			{
				WriteEnumString<TEnum>(value);
			}
			else
			{
				WriteEnumInteger<TEnum>(value);
			}
		}

		public void WriteEnum(Enum? value)
		{
			var fmt = m_attributes?.EnumFormat ?? JsonEnumFormat.Inherits;
			if ((fmt == JsonEnumFormat.Inherits && m_enumAsString) || fmt == JsonEnumFormat.String)
			{
				WriteEnumString(value);
			}
			else
			{
				WriteEnumInteger(value);
			}
		}

		public void WriteValue(decimal value)
		{
			// note: we do not add '.0' for integers, since 'decimal' could be used to represent any number (integer or floats) in dynamic or scripted languages (like javascript), and we want to be able to round-trip: "1" => (decimal) 1 => "1"
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(decimal? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Half value)
		{
			// special case for NaN and +/-Infinity that require specific tokens, depending on the configuration
			if (!Half.IsFinite(value))
			{
				m_buffer.Write(
					Half.IsNaN(value) ? CrystalJsonFormatter.GetNaNToken(m_floatFormat)
					: value > default(Half) ? CrystalJsonFormatter.GetPositiveInfinityToken(m_floatFormat)
					: CrystalJsonFormatter.GetNegativeInfinityToken(m_floatFormat)
				);
			}
			else
			{
				m_buffer.Write(value);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Half? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

#if NET8_0_OR_GREATER

		public void WriteValue(Int128 value)
		{
			m_buffer.Write(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Int128? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(UInt128 value)
		{
			m_buffer.Write(value);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(UInt128? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

#endif

		/// <summary>Write a <c>DateTime</c>, using the configured formatting</summary>
		public void WriteValue(DateTime value)
		{
			switch (m_dateFormat)
			{
				// CrystalJsonSettings.DateFormat.Default:
				// CrystalJsonSettings.DateFormat.TimeStampIso8601:
				default:
				{  // ISO 8601 "YYYY-MM-DDTHH:MM:SS.00000"
					WriteDateTimeIso8601(value);
					break;
				}
				case CrystalJsonSettings.DateFormat.Microsoft:
				{ // "\/Date(#####)\/" for UTC, or "\/Date(####+HHMM)\/" for LocalTime
					WriteDateTimeMicrosoft(value);
					break;
				}
				case CrystalJsonSettings.DateFormat.JavaScript:
				{ // "new Date(123456789)"
					WriteDateTimeJavaScript(value);
					break;
				}
			}
		}

		/// <summary>Write a nullable <c>DateTime</c>, using the configured formatting</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(DateTime? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		/// <summary>Write a <c>DateTimeOffset</c>, using the configured formatting</summary>
		public void WriteValue(DateTimeOffset value)
		{
			switch(m_dateFormat)
			{
				// CrystalJsonSettings.DateFormat.Default:
				// CrystalJsonSettings.DateFormat.TimeStampIso8601:
				default:
				{  // ISO 8601 "YYYY-MM-DDTHH:MM:SS.00000"
					WriteDateTimeIso8601(value);
					break;
				}
				case CrystalJsonSettings.DateFormat.Microsoft:
				{ // "\/Date(#####)\/" pour UTC, ou "\/Date(####+HHMM)\/" pour LocalTime
					WriteDateTimeMicrosoft(value);
					break;
				}
				case CrystalJsonSettings.DateFormat.JavaScript:
				{ // "new Date(123456789)"
					WriteDateTimeJavaScript(value);
					break;
				}
			}
		}

		/// <summary>Write a nullable <c>DateTimeOffset</c>, using the configured formatting</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(DateTimeOffset? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		/// <summary>Writes a <see cref="DateOnly"/> value, using the configured formatting</summary>
		public void WriteValue(DateOnly value)
		{
			switch (m_dateFormat)
			{
				// CrystalJsonSettings.DateFormat.Default:
				// CrystalJsonSettings.DateFormat.TimeStampIso8601:
				default:
				{  // ISO 8601 "YYYY-MM-DDTHH:MM:SS.00000"
					WriteDateOnlyIso8601(value);
					break;
				}
				case CrystalJsonSettings.DateFormat.Microsoft:
				{ // "\/Date(#####)\/" for UTC, or "\/Date(####+HHMM)\/" for LocalTime
					WriteDateTimeMicrosoft(value.ToDateTime(default));
					break;
				}
				case CrystalJsonSettings.DateFormat.JavaScript:
				{ // "new Date(123456789)"
					WriteDateTimeJavaScript(value.ToDateTime(default));
					break;
				}
			}
		}

		/// <summary>Writes a <see cref="TimeOnly"/> value, using the configured formatting</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(DateOnly? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		/// <summary>Writes a <see cref="TimeOnly"/> value, using the configured formatting</summary>
		public void WriteValue(TimeOnly value)
		{
			if (value == TimeOnly.MinValue)
			{
				m_buffer.Write(JsonTokens.Zero);
			}
			else
			{
				WriteValue(value.ToTimeSpan().TotalSeconds);
			}
		}

		/// <summary>Writes a <see cref="TimeOnly"/> value, using the configured formatting</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(TimeOnly? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		/// <summary>Write a date, using Microsoft's custom encoding <c>"\/Date(....)\/"</c></summary>
		public void WriteDateTimeMicrosoft(DateTime date)
		{
			if (date == DateTime.MinValue)
			{ // no explicit timezone
				m_buffer.Write(JsonTokens.MicrosoftDateTimeMinValue);
			}
			else if (date == DateTime.MaxValue)
			{ // no explicit timezone
				m_buffer.Write(JsonTokens.MicrosoftDateTimeMaxValue);
			}
			else
			{ // "\/Date(######)\/" or "\/Date(######+HHMM)\/"

				var sb = new StringBuilder(36)
					.Append(JsonTokens.DateBeginMicrosoft)
					.Append(CrystalJson.DateToJavaScriptTicks(date).ToString(null, NumberFormatInfo.InvariantInfo));
				if (date.Kind != DateTimeKind.Utc)
				{ // specify the timezone, so that it can correctly be converted to LocalTime afterward
					// => "/Date(.....+HHMM)/" or "/Date(...-HHMM)/"
					var offset = TimeZoneInfo.Local.GetUtcOffset(date);
					WriteDateTimeMicrosoftTimeZone(sb, offset);
				}
				sb.Append(JsonTokens.DateEndMicrosoft);
				m_buffer.Write(sb.ToString());
			}
		}

		/// <summary>Write a date with offset, using Microsoft's custom encoding <c>"\/Date(....)\/"</c></summary>
		public void WriteDateTimeMicrosoft(DateTimeOffset date)
		{
			if (date == DateTimeOffset.MinValue)
			{ // pour éviter de s'embrouiller avec les TimeZones...
				m_buffer.Write(JsonTokens.MicrosoftDateTimeMinValue);
			}
			else if (date == DateTimeOffset.MaxValue)
			{ // idem
				m_buffer.Write(JsonTokens.MicrosoftDateTimeMaxValue);
			}
			else
			{ // "\/Date(######+HHMM)\/"
				var sb = new StringBuilder(36)
					.Append(JsonTokens.DateBeginMicrosoft)
					.Append(CrystalJson.DateToJavaScriptTicks(date).ToString(null, NumberFormatInfo.InvariantInfo));
				// specify the timezone, so that it can correctly be converted to LocalTime afterward
				// => "/Date(.....+HHMM)/" or "/Date(...-HHMM)/"
				var offset = date.Offset;
				WriteDateTimeMicrosoftTimeZone(sb, offset);
				sb.Append(JsonTokens.DateEndMicrosoft);
				m_buffer.Write(sb.ToString());
			}
		}

		/// <summary>Append the "+HHMM"/"-HHMM" suffix that correspond to the UTC offset of a TimeZone</summary>
		internal static void WriteDateTimeMicrosoftTimeZone(StringBuilder sb, TimeSpan offset)
		{
			//note: if GMT-xxx, Hours et Minutes are also negative !!!
			int h = Math.Abs(offset.Hours);
			int m = Math.Abs(offset.Minutes);
			sb.Append(offset < TimeSpan.Zero ? '-' : '+').Append((char)('0' + (h / 10))).Append((char)('0' + (h % 10))).Append((char)('0' + (m / 10))).Append((char)('0' + (m % 10)));
		}

		/// <summary>Write a date using the ISO 8601 format: <c>"YYYY-MM-DDTHH:mm:ss.ffff+TZ"</c></summary>
		public void WriteDateTimeIso8601(DateTime date)
		{
			if (date == DateTime.MinValue)
			{ // MinValue is serialized as the emtpy string
				m_buffer.Write(JsonTokens.EmptyString);
			}
			else if (date == DateTime.MaxValue)
			{ // MaxValue should not specify a timezone
				m_buffer.Write(JsonTokens.Iso8601DateTimeMaxValue);
			}
			else
			{
				Span<char> buf = stackalloc char[CrystalJsonFormatter.ISO8601_MAX_FORMATTED_SIZE];
				m_buffer.Write(CrystalJsonFormatter.FormatIso8601DateTime(buf, date, date.Kind, null, '"'));
			}
		}

		/// <summary>Write a date with offset using the ISO 8601 format: <c>"YYYY-MM-DDTHH:mm:ss.ffff+TZ"</c></summary>
		public void WriteDateTimeIso8601(DateTimeOffset date)
		{
			if (date == default)
			{ // MinValue (== default) is serialized as an empty string
				m_buffer.Write(JsonTokens.EmptyString);
			}
			else if (date == DateTimeOffset.MaxValue)
			{ // MaxValue should not specify any timezone
				m_buffer.Write(JsonTokens.Iso8601DateTimeMaxValue);
			}
			else
			{
				Span<char> buf = stackalloc char[CrystalJsonFormatter.ISO8601_MAX_FORMATTED_SIZE];
				m_buffer.Write(CrystalJsonFormatter.FormatIso8601DateTime(buf, date.DateTime, DateTimeKind.Local, date.Offset, '"'));
			}
		}

		/// <summary>Write a date using the ISO 8601 format: <c>"YYYY-MM-DD"</c></summary>
		public void WriteDateOnlyIso8601(DateOnly date)
		{
			if (date == DateOnly.MinValue)
			{ // MinValue is serialized as the emtpy string
				m_buffer.Write(JsonTokens.EmptyString);
			}
			else
			{
				Span<char> buf = stackalloc char[CrystalJsonFormatter.ISO8601_MAX_FORMATTED_SIZE];
				m_buffer.Write(CrystalJsonFormatter.FormatIso8601DateOnly(buf, date, '"'));
			}
		}

		/// <summary>Write a date, using the Javascript format: <c>new Date(123456789)</c></summary>
		public void WriteDateTimeJavaScript(DateTime date)
		{
			if (date == DateTime.MinValue)
			{ // no timezone
				m_buffer.Write(JsonTokens.JavaScriptDateTimeMinValue);
			}
			else if (date == DateTime.MaxValue)
			{ // no timezone
				m_buffer.Write(JsonTokens.JavaScriptDateTimeMaxValue);
			}
			else
			{ // "new Date(#####)"
				m_buffer.Write(JsonTokens.DateBeginJavaScript);
				m_buffer.Write(CrystalJson.DateToJavaScriptTicks(date).ToString(NumberFormatInfo.InvariantInfo));
				m_buffer.Write(')');
			}
		}

		/// <summary>Write a date with offset, using the Javascript format: <c>new Date(123456789)</c></summary>
		public void WriteDateTimeJavaScript(DateTimeOffset date)
		{
			if (date == DateTimeOffset.MinValue)
			{ // no timezone
				m_buffer.Write(JsonTokens.JavaScriptDateTimeMinValue);
			}
			else if (date == DateTimeOffset.MaxValue)
			{ // no timezone
				m_buffer.Write(JsonTokens.JavaScriptDateTimeMaxValue);
			}
			else
			{ // "new Date(#####)"
				m_buffer.Write(JsonTokens.DateBeginJavaScript);
				m_buffer.Write(CrystalJson.DateToJavaScriptTicks(date).ToString(null, NumberFormatInfo.InvariantInfo));
				m_buffer.Write(')');
			}
		}

		public void WriteValue(TimeSpan value)
		{
			if (value == TimeSpan.Zero)
			{
				m_buffer.Write(JsonTokens.Zero);
			}
			else
			{
				WriteValue(value.TotalSeconds);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(TimeSpan? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Guid value)
		{
			if (value == Guid.Empty)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!m_javascript)
			{
				m_buffer.Write('"');
				m_buffer.Write(value);
				m_buffer.Write('"');
			}
			else
			{
				m_buffer.Write('\'');
				m_buffer.Write(value);
				m_buffer.Write('\'');
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Guid? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Uuid128 value)
		{
			if (value == Uuid128.Empty)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!m_javascript)
			{
				m_buffer.Write('"');
				m_buffer.Write(value);
				m_buffer.Write('"');
			}
			else
			{
				m_buffer.Write('\'');
				m_buffer.Write(value);
				m_buffer.Write('\'');
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Uuid128? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Uuid96 value)
		{
			if (value == Uuid96.Empty)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!m_javascript)
			{
				m_buffer.Write('"');
				m_buffer.Write(value.ToString());
				m_buffer.Write('"');
			}
			else
			{
				m_buffer.Write('\'');
				m_buffer.Write(value.ToString());
				m_buffer.Write('\'');
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Uuid96? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Uuid80 value)
		{
			if (value == Uuid80.Empty)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!m_javascript)
			{
				m_buffer.Write('"');
				m_buffer.Write(value.ToString());
				m_buffer.Write('"');
			}
			else
			{
				m_buffer.Write('\'');
				m_buffer.Write(value.ToString());
				m_buffer.Write('\'');
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Uuid80? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(Uuid64 value)
		{
			if (value == Uuid64.Empty)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else if (!m_javascript)
			{
				m_buffer.Write('"');
				m_buffer.Write(value);
				m_buffer.Write('"');
			}
			else
			{
				m_buffer.Write('\'');
				m_buffer.Write(value);
				m_buffer.Write('\'');
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Uuid64? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(NodaTime.Duration value)
		{
			if (value == NodaTime.Duration.Zero)
			{
				m_buffer.Write(JsonTokens.Zero);
				return;
			}

			double sec = value.TotalSeconds;
			if (sec < 100_000_000)
			{
				WriteValue(value.TotalSeconds);
				return;
			}

			// we must decompose (days, nanosOfDays) into (seconds, nanosOfSeconds)
			int days = value.Days;
			long nanosOfDay = value.NanosecondOfDay;
			long secsOfDay = nanosOfDay / 1_000_000_000;
			long nanos = nanosOfDay - (secsOfDay * 1_000_000_000);
			long secs = secsOfDay + (days * 86400);

			CrystalJsonFormatter.WriteFixedIntegerWithDecimalPartUnsafe(ref m_buffer, secs, nanos, 9);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.Duration? value)
		{
			if (value.HasValue) WriteValue(value.Value); else WriteNull();
		}

		public void WriteValue(NodaTime.Instant date)
		{
			if (date == NodaTime.Instant.MinValue)
			{ // MinValue is serialized as the empty string
				m_buffer.Write(JsonTokens.EmptyString);
			}
			else if (date == NodaTime.Instant.MaxValue)
			{ // MaxValue does not have any timezone
				m_buffer.Write(JsonTokens.Iso8601DateTimeMaxValue);
			}
			else
			{ // "2013-07-26T16:45:20.1234567Z"

				// "uuuu'-'MM'-'dd'T'HH':'mm':'ss;FFFFFFFFF'Z'"
				if (date >= NodaConstants.BclEpoch)
				{
					WriteValue(date.ToDateTimeUtc());
				}
				else
				{
					WriteValue(InstantPattern.ExtendedIso.Format(date));
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.Instant? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.LocalDateTime date)
		{
			// "1988-04-19T00:35:56" or "1988-04-19T00:35:56.342" (no 'Z' suffix or timezone)
			WriteValue(CrystalJsonNodaPatterns.LocalDateTimes.Format(date));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.LocalDateTime? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.ZonedDateTime date)
		{
			WriteValue(CrystalJsonNodaPatterns.ZonedDateTimes.Format(date));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.ZonedDateTime? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.OffsetDateTime date)
		{
			WriteValue(CrystalJsonNodaPatterns.OffsetDateTimes.Format(date));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.OffsetDateTime? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.Offset offset)
		{
			// "+01:00"
			WriteValue(CrystalJsonNodaPatterns.Offsets.Format(offset));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.Offset? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.LocalDate date)
		{
			// "2014-07-22"
			WriteValue(CrystalJsonNodaPatterns.LocalDates.Format(date));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.LocalDate? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.LocalTime time)
		{
			// "11:39:42.123457"
			WriteValue(CrystalJsonNodaPatterns.LocalTimes.Format(time));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(NodaTime.LocalTime? value)
		{
			if (value.HasValue)
			{
				WriteValue(value.Value);
			}
			else
			{
				WriteNull();
			}
		}

		public void WriteValue(NodaTime.DateTimeZone? zone)
		{
			if (zone == null)
			{
				WriteNull();
			}
			else
			{ // "Europe/Paris"
				WriteValue(zone.Id);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Version? value)
		{
			WriteValue(value?.ToString());
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(System.Net.IPAddress? value)
		{
			WriteValue(value?.ToString());
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteValue(Uri? value)
		{
			WriteValue(value?.OriginalString);
		}

		#endregion

		public void WriteBuffer(byte[]? bytes)
		{
			if (bytes == null)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else
			{
				WriteBuffer(new ReadOnlySpan<byte>(bytes));
			}
		}

		public void WriteBuffer(byte[]? bytes, int offset, int count)
		{
			if (bytes == null)
			{
				m_buffer.Write(JsonTokens.Null);
			}
			else
			{
				WriteBuffer(bytes.AsSpan(offset, count));
			}
		}

		public void WriteBuffer(ReadOnlySpan<byte> bytes)
		{
			if (bytes.Length == 0)
			{
				m_buffer.Write(JsonTokens.EmptyString);
			}
			else
			{ // note: Base64 without any <'> or <">, so no need to escape it!
				m_buffer.Write('"');
				Base64Encoding.EncodeTo(ref m_buffer, bytes);
				m_buffer.Write('"');
			}
		}

		public void WriteBuffer(Slice bytes)
		{
			if (bytes.Count == 0)
			{
				m_buffer.Write(bytes.Array == null! ? JsonTokens.Null : JsonTokens.EmptyString);
			}
			else
			{ // note: Base64 without any <'> or <">, so no need to escape it!
				m_buffer.Write('"');
				Base64Encoding.EncodeTo(ref m_buffer, bytes.Span);
				m_buffer.Write('"');
			}
		}

		#endregion

		#region Field Writers...

		public void WriteFieldNull(string name)
		{
			if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteFieldNull(in JsonEncodedPropertyName name)
		{
			if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteField(string name, string? value)
		{
			if (value != null || !m_discardNulls)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, string? value)
		{
			if (value != null || !m_discardNulls)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, StringBuilder? value)
		{
			if (value != null || !m_discardNulls)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, ReadOnlySpan<char> value)
		{
			WriteName(name);
			WriteValue(value);
		}

		public void WriteField(string name, ReadOnlyMemory<char> value)
		{
			WriteName(name);
			WriteValue(value.Span);
		}

		public void WriteField(string name, bool value)
		{
			if (value || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, bool value)
		{
			if (value || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, bool? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, bool? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, int value)
		{
			if (value != 0 || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, int value)
		{
			if (value != 0 || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, int? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, int? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, long value)
		{
			if (value != 0L || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, long value)
		{
			if (value != 0L || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, long? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, long? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, float value)
		{
			// ReSharper disable once CompareOfFloatsByEqualityOperator
			if (value != 0f || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, float? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, float? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, double value)
		{
			// ReSharper disable once CompareOfFloatsByEqualityOperator
			if (value != 0d || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, double? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, double? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

#if NET8_0_OR_GREATER

		public void WriteField(string name, Half value)
		{
			// ReSharper disable once CompareOfFloatsByEqualityOperator
			if (value != default || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Half? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

#endif

		public void WriteField(string name, DateTime value)
		{
			if (value != DateTime.MinValue || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, DateTime value)
		{
			if (value != DateTime.MinValue || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, DateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, DateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, Guid value)
		{
			if (value != Guid.Empty || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Guid value)
		{
			if (value != Guid.Empty || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Guid? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Guid? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, Uuid128 value)
		{
			if (value != Uuid128.Empty|| !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid128 value)
		{
			if (value != Uuid128.Empty|| !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Uuid128? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid128? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, Uuid96 value)
		{
			if (value != Uuid96.Empty || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid96 value)
		{
			if (value != Uuid96.Empty || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Uuid96? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid96? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, Uuid80 value)
		{
			if (value != Uuid80.Empty || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid80 value)
		{
			if (value != Uuid80.Empty || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Uuid80? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid80? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, Uuid64 value)
		{
			if (value != Uuid64.Empty || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, Uuid64 value)
		{
			if (value != Uuid64.Empty || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, Uuid64? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		#region NodaTime Types...

		public void WriteField(string name, NodaTime.Instant value)
		{
			if (value.ToUnixTimeTicks() != 0 || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Instant value)
		{
			if (value.ToUnixTimeTicks() != 0 || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, NodaTime.Instant? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Instant? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(string name, NodaTime.Duration value)
		{
			if (value.BclCompatibleTicks != 0 || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Duration value)
		{
			if (value.BclCompatibleTicks != 0 || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, NodaTime.Duration? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Duration? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.ZonedDateTime value)
		{
			//TODO: defaults?
			WriteName(name);
			WriteValue(value);
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.ZonedDateTime value)
		{
			//TODO: defaults?
			WriteName(in name);
			WriteValue(value);
		}

		public void WriteField(string name, NodaTime.ZonedDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.ZonedDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.LocalDateTime value)
		{
			//TODO: defaults?
			WriteName(name);
			WriteValue(value);
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.LocalDateTime value)
		{
			//TODO: defaults?
			WriteName(in name);
			WriteValue(value);
		}

		public void WriteField(string name, NodaTime.LocalDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.LocalDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.LocalDate value)
		{
			//TODO: defaults?
			WriteName(name);
			WriteValue(value);
		}

		public void WriteField(string name, NodaTime.LocalDate? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.LocalDate? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.LocalTime value)
		{
			if (value.TickOfDay != 0 || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.LocalTime value)
		{
			if (value.TickOfDay != 0 || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, NodaTime.LocalTime? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.LocalTime? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.OffsetDateTime value)
		{
			//TODO: defaults?
			WriteName(name);
			WriteValue(value);
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.OffsetDateTime value)
		{
			//TODO: defaults?
			WriteName(in name);
			WriteValue(value);
		}

		public void WriteField(string name, NodaTime.OffsetDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.OffsetDateTime? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.Offset value)
		{
			if (value.Milliseconds != 0 || !m_discardDefaults)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Offset value)
		{
			if (value.Milliseconds != 0 || !m_discardDefaults)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		public void WriteField(string name, NodaTime.Offset? value)
		{
			if (value.HasValue)
			{
				WriteName(name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(name);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.Offset? value)
		{
			if (value.HasValue)
			{
				WriteName(in name);
				WriteValue(value.Value);
			}
			else
			{
				WriteFieldNull(in name);
			}
		}

		public void WriteField(string name, NodaTime.DateTimeZone? value)
		{
			if (value != null || !m_discardNulls)
			{
				WriteName(name);
				WriteValue(value);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, NodaTime.DateTimeZone? value)
		{
			if (value != null || !m_discardNulls)
			{
				WriteName(in name);
				WriteValue(value);
			}
		}

		#endregion

		/// <summary>Tests if the specified value would have been discarded when calling <see cref="WriteField(string,JsonValue)"/></summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool WillBeDiscarded(JsonValue? value) => value switch
		{
			null => m_discardNulls,
			JsonNull => !ReferenceEquals(value, JsonNull.Null) && m_discardNulls, // note: JsonNull.Null is NEVER discarded
			JsonBoolean b => m_discardDefaults && !b.Value,
			JsonString or JsonNumber or JsonDateTime => m_discardDefaults && value.IsDefault,
			_ => false // arrays and objects are NEVER discarded
		};

		public void WriteField(string name, JsonValue? value)
		{
			value ??= JsonNull.Null;
			if (!WillBeDiscarded(value))
			{
				WriteName(name);
				value.JsonSerialize(this);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, JsonValue? value)
		{
			value ??= JsonNull.Null;
			if (!WillBeDiscarded(value))
			{
				WriteName(in name);
				value.JsonSerialize(this);
			}
		}

		//note: these overloads only exist to prevent "WriteField(..., new JsonObject())" to call WriteField<JsonObject>(..., ...), instead of WriteField(..., JsonValue)

		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonNull? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonObject? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonArray? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonString? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonNumber? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonBoolean? value) => WriteField(name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(string name, JsonDateTime? value) => WriteField(name, (JsonValue?) value);

		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonNull? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonObject? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonArray? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonString? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonNumber? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonBoolean? value) => WriteField(in name, (JsonValue?) value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)] public void WriteField(in JsonEncodedPropertyName name, JsonDateTime? value) => WriteField(in name, (JsonValue?) value);

		public void WriteField(string name, object? value, Type declaredType)
		{
			if (value is not null || !m_discardNulls)
			{
				WriteName(name);
				CrystalJsonVisitor.VisitValue(value, declaredType, this);
			}
		}

		public void WriteField(in JsonEncodedPropertyName name, object? value, Type declaredType)
		{
			if (value is not null || !m_discardNulls)
			{
				WriteName(in name);
				CrystalJsonVisitor.VisitValue(value, declaredType, this);
			}
		}

		public void WriteField<T>(string name, T value)
		{
			if (value is not null)
			{
				WriteName(name);
				VisitValue(value);
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteField<T>(in JsonEncodedPropertyName name, T value)
		{
			if (value is not null)
			{
				WriteName(in name);
				VisitValue(value);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteField<T>(string name, T? value)
			where T : struct
		{
			if (value.HasValue)
			{
				WriteName(name);
				VisitValue<T>(value.Value);
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteField<T>(in JsonEncodedPropertyName name, T? value)
			where T : struct
		{
			if (value.HasValue)
			{
				WriteName(in name);
				VisitValue<T>(value.Value);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteField<TValue>(in JsonEncodedPropertyName name, TValue? value, IJsonSerializer<TValue> serializer)
		{
			if (value is not null)
			{
				WriteName(in name);
				serializer.JsonSerialize(this, value);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray(string name, string?[]? items)
		{
			if (items is not null)
			{
				WriteName(name);
				WriteArray(items);
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteFieldArray(in JsonEncodedPropertyName name, string?[]? items)
		{
			if (items is not null)
			{
				WriteName(in name);
				WriteArray(items);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray(string name, ReadOnlySpan<string> items)
		{
			WriteName(name);
			WriteArray(items);
		}

		public void WriteFieldArray(in JsonEncodedPropertyName name, ReadOnlySpan<string> items)
		{
			WriteName(in name);
			WriteArray(items);
		}

		public void WriteFieldArray(string name, IEnumerable<string>? items)
		{
			if (items is not null)
			{
				WriteName(name);
				if (Doxense.Linq.Buffer<string>.TryGetSpan(items, out var span))
				{
					WriteArray(span);
				}
				else
				{
					var state = BeginArray();
					foreach (var item in items)
					{
						WriteFieldSeparator();
						VisitValue(item);
					}
					EndArray(state);
				}
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteFieldArray(in JsonEncodedPropertyName name, IEnumerable<string>? items)
		{
			if (items is not null)
			{
				WriteName(in name);
				if (Doxense.Linq.Buffer<string>.TryGetSpan(items, out var span))
				{
					WriteArray(span);
				}
				else
				{
					var state = BeginArray();
					foreach (var item in items)
					{
						WriteFieldSeparator();
						VisitValue(item);
					}
					EndArray(state);
				}
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(string name, T[]? items)
		{
			if (items is not null)
			{
				WriteName(name);
				WriteArray<T>(items);
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, T[]? items)
		{
			if (items is not null)
			{
				WriteName(in name);
				WriteArray<T>(items);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(string name, ReadOnlySpan<T> array)
		{
			WriteName(name);
			WriteArray<T>(array);
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, ReadOnlySpan<T> array)
		{
			WriteName(in name);
			WriteArray<T>(array);
		}

		public void WriteFieldArray<T>(string name, IEnumerable<T>? items)
		{
			if (items is not null)
			{
				WriteName(name);
				if (Doxense.Linq.Buffer<T>.TryGetSpan(items, out var span))
				{
					WriteArray<T>(span);
				}
				else
				{
					var state = BeginArray();
					foreach (var item in items)
					{
						WriteFieldSeparator();
						VisitValue(item);
					}
					EndArray(state);
				}
			}
			else if (!m_discardNulls)
			{
				WriteName(name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, IEnumerable<T>? items)
		{
			if (items is not null)
			{
				WriteName(in name);
				WriteArray(items);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, ReadOnlySpan<T> array, IJsonSerializer<T> serializer)
		{
			WriteName(in name);
			VisitArray(array, serializer);
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, T[]? items, IJsonSerializer<T> serializer)
		{
			if (items is not null)
			{
				WriteName(in name);
				VisitArray(items, serializer);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, List<T>? items, IJsonSerializer<T> serializer)
		{
			if (items is not null)
			{
				WriteName(in name);
				VisitArray(CollectionsMarshal.AsSpan(items), serializer);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldArray<T>(in JsonEncodedPropertyName name, IEnumerable<T>? items, IJsonSerializer<T> serializer)
		{
			if (items is not null)
			{
				WriteName(in name);
				VisitArray(items, serializer);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		public void WriteFieldDictionary<T>(in JsonEncodedPropertyName name, IDictionary<string, T>? items, IJsonSerializer<T> serializer)
		{
			if (items is not null)
			{
				WriteName(in name);
				VisitDictionary(items, serializer);
			}
			else if (!m_discardNulls)
			{
				WriteName(in name);
				WriteNull();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void VisitValue(object? value, Type declaredType)
		{
			CrystalJsonVisitor.VisitValue(value, declaredType, this);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void VisitValue<T>(T value)
		{
			CrystalJsonVisitor.VisitValue<T>(value, this);
		}

		public void VisitArray<T>(T[]? array, IJsonSerializer<T> serializer)
		{
			if (array is null)
			{
				WriteNull();
			}
			else
			{
				VisitArray(new ReadOnlySpan<T>(array), serializer);
			}
		}

		public void VisitArray<T>(List<T>? array, IJsonSerializer<T> serializer)
		{
			if (array is null)
			{
				WriteNull();
			}
			else
			{
				VisitArray(CollectionsMarshal.AsSpan(array), serializer);
			}
		}

		public void VisitArray<T>(ReadOnlySpan<T> array, IJsonSerializer<T> serializer)
		{
			if (array.Length == 0)
			{
				WriteEmptyArray();
				return;
			}

			var state = BeginArray();

			WriteHeadSeparator();
			serializer.JsonSerialize(this, array[0]);

			for(int i = 1; i < array.Length; i++)
			{
				WriteTailSeparator();
				serializer.JsonSerialize(this, array[i]);
			}

			EndArray(state);
		}

		public void VisitArray<T>([InstantHandle] IEnumerable<T>? array, IJsonSerializer<T> serializer)
		{
			if (array is null)
			{
				WriteNull();
				return;
			}

			if (Doxense.Linq.Buffer<T>.TryGetSpan(array, out var span))
			{
				VisitArray(span, serializer);
			}
			else
			{
				var state = BeginArray();
				foreach (var item in array)
				{
					WriteFieldSeparator();
					serializer.JsonSerialize(this, item);
				}
				EndArray(state);
			}
		}

		public void VisitArray<T>([InstantHandle] IEnumerable<T>? array, Action<CrystalJsonWriter, T> action)
		{
			Contract.NotNull(action);
			if (array == null)
			{
				WriteNull();
			}
			else
			{
				var state = BeginArray();
				foreach (var item in array)
				{
					WriteFieldSeparator();
					action(this, item);
				}
				EndArray(state);
			}
		}

		/// <summary>Visite un collection d'éléments</summary>
		/// <typeparam name="T">Type des éléments d'une collection</typeparam>
		/// <param name="items"></param>
		public void WriteArray<T>([InstantHandle] IEnumerable<T>? items)
		{
			if (items is null)
			{
				WriteNull();
				return;
			}

			if (Doxense.Linq.Buffer<T>.TryGetSpan(items, out var span))
			{
				WriteArray<T>(span);
			}
			else
			{
				var state = BeginArray();
				foreach (var item in items)
				{
					WriteFieldSeparator();
					VisitValue(item);
				}

				EndArray(state);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteArray<T>(T[]? array)
		{
			if (array is not null)
			{
				WriteArray(new ReadOnlySpan<T>(array));
			}
			else if (!m_discardNulls)
			{
				WriteNull();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteArray<T>(T[] array, int offset, int count) => WriteArray((ReadOnlySpan<T>) array.AsSpan(offset, count));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteArray<T>(Span<T> array) => WriteArray<T>((ReadOnlySpan<T>) array);

		public void WriteArray<T>(ReadOnlySpan<T> array)
		{
			//TODO: check params ?

			if (array.Length == 0)
			{
				WriteEmptyArray();
				return;
			}

			var state = BeginArray();

			WriteHeadSeparator();
			CrystalJsonVisitor.VisitValue<T>(array[0], this);

			for(int i = 1; i < array.Length; i++)
			{
				WriteTailSeparator();
				CrystalJsonVisitor.VisitValue<T>(array[i], this);
			}

			EndArray(state);
		}

		public void WriteArray(ReadOnlySpan<string> array)
		{
			//TODO: check params ?

			if (array.Length == 0)
			{
				WriteEmptyArray();
				return;
			}

			var state = BeginArray();

			WriteHeadSeparator();
			WriteValue(array[0]);

			for(int i = 1; i < array.Length; i++)
			{
				WriteTailSeparator();
				WriteValue(array[0]);
			}

			EndArray(state);
		}

		public void WriteArray<TKey, TValue>(ICollection<KeyValuePair<TKey, TValue>>? source)
		{
			if (source == null)
			{
				WriteNull();
			}
			else if (source.Count == 0)
			{
				WriteEmptyArray();
			}
			else
			{
				var s1 = BeginArray();
				foreach (var kvp in source)
				{
					WriteFieldSeparator();
					var s2 = BeginArray();
					{
						WriteHeadSeparator();
						VisitValue<TKey>(kvp.Key);
						WriteTailSeparator();
						VisitValue<TValue>(kvp.Value);
					}
					EndArray(s2);
				}
				EndArray(s1);
			}
		}

		public void VisitDictionary<TValue>(IDictionary<string, TValue>? map, IJsonSerializer<TValue> serializer)
		{
			if (map is null)
			{
				WriteNull();
				return;
			}

			if (map.Count == 0)
			{ // empty => "{}"
				WriteEmptyObject(); // "{}"
				return;
			}

			var state = BeginObject();
			if (map is Dictionary<string, TValue> dict)
			{
				// we can use the struct enumerator
				foreach (var kvp in dict)
				{
					WriteNameEscaped(kvp.Key);
					serializer.JsonSerialize(this, kvp.Value);
				}
			}
			else
			{
				// this will allocate an enumerator
				foreach (var kvp in map)
				{
					WriteNameEscaped(kvp.Key);
					serializer.JsonSerialize(this, kvp.Value);
				}
			}
			EndObject(state); // "}"
		}

		public void WriteDictionary(IDictionary<string, object>? map)
		{
			CrystalJsonVisitor.VisitGenericObjectDictionary(map, this);
		}

		public void WriteDictionary(IDictionary<string, string>? map)
		{
			CrystalJsonVisitor.VisitStringDictionary(map, this);
		}

		public void WriteDictionary<TValue>(Dictionary<string, TValue>? map)
		{
			CrystalJsonVisitor.VisitGenericDictionary<TValue>(map, this);
		}

		public void VisitXmlNode(System.Xml.XmlNode? node)
		{
			CrystalJsonVisitor.VisitXmlNode(node, this);
		}

		#endregion

	}

}
