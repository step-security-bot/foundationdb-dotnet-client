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

namespace Doxense.Serialization.Json
{
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using Doxense.Memory;

	/// <summary>JSON Boolean (<see langword="true"/> or <see langword="false"/>)</summary>
	[DebuggerDisplay("JSON Boolean({m_value})")]
	[DebuggerNonUserCode]
	[PublicAPI]
	public sealed class JsonBoolean : JsonValue, IEquatable<bool>, IEquatable<JsonBoolean>, IComparable<JsonBoolean>
	{

		/// <summary>JSON value that is equal to <see langword="true"/></summary>
		/// <remarks>This singleton is immutable and can be cached</remarks>
		public static readonly JsonBoolean True = new(true);

		/// <summary>JSON value that is equal to <see langword="false"/></summary>
		/// <remarks>This singleton is immutable and can be cached</remarks>
		public static readonly JsonBoolean False = new(false);

		private readonly bool m_value;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal JsonBoolean(bool value) => m_value = value;

		/// <summary>Returns either <see cref="JsonBoolean.True"/> or <see cref="JsonBoolean.False"/></summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonBoolean Return(bool value) => value ? True : False;

		/// <summary>Returns either <see cref="JsonBoolean.True"/>, <see cref="JsonBoolean.False"/> or <see cref="JsonNull.Null"/></summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonValue Return(bool? value) => value == null ? JsonNull.Null : value.Value ? JsonBoolean.True : JsonBoolean.False;

		public bool Value => m_value;

		#region JsonValue Members...

		public override JsonType Type => JsonType.Boolean;

		public override bool IsDefault => !m_value;

		public override bool IsReadOnly => true; //note: booleans are immutable

		public override object ToObject() => m_value;

		public override T? Bind<T>(T? defaultValue = default, ICrystalJsonTypeResolver? resolver = null) where T : default
		{
			#region <JIT_HACK>
			// pattern recognized and optimized by the JIT, only in Release build
#if !DEBUG
			if (typeof(T) == typeof(bool)) return (T) (object) ToBoolean();
			if (typeof(T) == typeof(byte)) return (T) (object) ToByte();
			if (typeof(T) == typeof(sbyte)) return (T) (object) ToSByte();
			if (typeof(T) == typeof(char)) return (T) (object) ToChar();
			if (typeof(T) == typeof(short)) return (T) (object) ToInt16();
			if (typeof(T) == typeof(ushort)) return (T) (object) ToUInt16();
			if (typeof(T) == typeof(int)) return (T) (object) ToInt32();
			if (typeof(T) == typeof(uint)) return (T) (object) ToUInt32();
			if (typeof(T) == typeof(ulong)) return (T) (object) ToUInt64();
			if (typeof(T) == typeof(long)) return (T) (object) ToInt64();
			if (typeof(T) == typeof(float)) return (T) (object) ToSingle();
			if (typeof(T) == typeof(double)) return (T) (object) ToDouble();
			if (typeof(T) == typeof(decimal)) return (T) (object) ToDecimal();
			if (typeof(T) == typeof(TimeSpan)) return (T) (object) ToTimeSpan();
			if (typeof(T) == typeof(DateTime)) return (T) (object) ToDateTime();
			if (typeof(T) == typeof(DateTimeOffset)) return (T) (object) ToDateTimeOffset();
			if (typeof(T) == typeof(DateOnly)) return (T) (object) ToDateOnly();
			if (typeof(T) == typeof(TimeOnly)) return (T) (object) ToTimeOnly();
			if (typeof(T) == typeof(Guid)) return (T) (object) ToGuid();
			if (typeof(T) == typeof(Uuid128)) return (T) (object) ToUuid128();
			if (typeof(T) == typeof(Uuid96)) return (T) (object) ToUuid96();
			if (typeof(T) == typeof(Uuid80)) return (T) (object) ToUuid80();
			if (typeof(T) == typeof(Uuid64)) return (T) (object) ToUuid64();
			if (typeof(T) == typeof(NodaTime.Instant)) return (T) (object) ToInstant();
			if (typeof(T) == typeof(NodaTime.Duration)) return (T) (object) ToDuration();
#endif
			#endregion

			return (T?) BindNative(this, m_value, typeof(T), resolver) ?? defaultValue;
		}

		public override object? Bind(Type? type, ICrystalJsonTypeResolver? resolver = null) => BindNative(this, m_value, type, resolver);

		internal override bool IsSmallValue() => true;

		internal override bool IsInlinable() => true;

		#endregion

		#region IEquatable<...>

		public override bool Equals(object? value)
		{
			return value switch
			{
				JsonValue j => Equals(j),
				bool b => m_value == b,
				_ => false
			};
		}

		/// <inheritdoc />
		public override bool ValueEquals<TValue>(TValue? value, IEqualityComparer<TValue>? comparer = null) where TValue : default
		{
			if (default(TValue) is null)
			{
				if (value is null)
				{ // null != false
					return false;
				}

				if (typeof(TValue) == typeof(bool?))
				{ // we already know it's not null
					return m_value == (bool) (object) value!;
				}

				if (value is JsonBoolean j)
				{ // only JsonBoolean would match...
					return j.m_value == (bool) (object) value!;
				}
			}
			else
			{
				if (typeof(TValue) == typeof(bool))
				{ // direct match
					return m_value == (bool) (object) value!;
				}
			}

			return false;
		}

		public override bool Equals(JsonValue? value)
		{
			return value is JsonBoolean b && b.m_value == m_value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(JsonBoolean? obj) => obj is not null && obj.m_value == m_value;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(bool value) => m_value == value;

		public override int GetHashCode() => m_value ? 1 : 0;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static implicit operator bool(JsonBoolean? obj) => obj?.m_value == true;
		//TODO: REVIEW: is this usefull ? when do we have a variable of explicit type JsonBoolean?

		#endregion

		#region IComparable<...>

		public override int CompareTo(JsonValue? other)
		{
			if (other.IsNullOrMissing()) return +1;
			if (other is JsonBoolean b)
			{
				return m_value.CompareTo(b.m_value);
			}
			return base.CompareTo(other);
		}

		public int CompareTo(JsonBoolean? other)
		{
			return other != null ? m_value.CompareTo(other.Value) : +1;
		}

		#endregion

		#region IJsonConvertible...

		public override string ToJson(CrystalJsonSettings? settings = null) => m_value ? JsonTokens.True : JsonTokens.False;

		public override string ToString() => m_value ? JsonTokens.True : JsonTokens.False;

		public override bool ToBoolean(bool _ = false) => m_value;

		public override byte ToByte(byte _ = 0) => m_value ? (byte) 1 : default(byte);

		public override sbyte ToSByte(sbyte _ = 0) => m_value ? (sbyte)1 : default(sbyte);

		public override char ToChar(char _ = '\0') => m_value ? 'Y' : 'N';

		public override short ToInt16(short _ = 0) => m_value ? (short) 1 : default(short);

		public override ushort ToUInt16(ushort _ = 0) => m_value ? (ushort) 1 : default(ushort);

		public override int ToInt32(int _ = 0) => m_value ? 1 : 0;

		public override uint ToUInt32(uint _ = 0) => m_value ? 1U : 0U;

		public override long ToInt64(long _ = 0) => m_value ? 1L : 0L;

		public override ulong ToUInt64(ulong _ = 0) => m_value ? 1UL : 0UL;

#if NET8_0_OR_GREATER

		public override Int128 ToInt128(Int128 _ = default) => m_value ? Int128.One : Int128.Zero;

		public override UInt128 ToUInt128(UInt128 _ = default) => m_value ? UInt128.One : UInt128.Zero;

#endif

		public override float ToSingle(float _ = default) => m_value ? 1f : 0f;

		public override double ToDouble(double _ = default) => m_value ? 1d : 0d;


#if NET8_0_OR_GREATER
		public override Half ToHalf(Half _ = default) => m_value ? Half.One : Half.Zero;
#else
		private static readonly Half HalfZero = (Half) 0;
		private static readonly Half HalfOne = (Half) 1;
		public override Half ToHalf(Half _ = default) => m_value ? HalfOne : HalfZero;
#endif

		public override decimal ToDecimal(decimal _ = default) => m_value ? 1m : 0m;

		private static readonly Guid AllF = new(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 });

		public override Guid ToGuid(Guid _ = default) => m_value ? AllF : Guid.Empty;

		public override Uuid64 ToUuid64(Uuid64 _ = default) => m_value ? new Uuid64(-1) : default(Uuid64);

		#endregion

		#region IJsonSerializable

		public override void JsonSerialize(CrystalJsonWriter writer)
		{
			writer.WriteValue(m_value);
		}

		/// <inheritdoc />
		public override bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
		{
			var literal = m_value ? JsonTokens.True : JsonTokens.False;

			if (destination.Length < literal.Length)
			{
				charsWritten = 0;
				return false;
			}

			literal.CopyTo(destination);
			charsWritten = literal.Length;
			return true;
		}

		#endregion

		#region ISliceSerializable

		public override void WriteTo(ref SliceWriter writer)
		{
			if (m_value)
			{ // 'true' => 74 72 75 65
				writer.WriteBytes("true"u8);
			}
			else
			{ // 'false' => 66 61 6C 73 65
				writer.WriteBytes("false"u8);
			}
		}

		#endregion

	}

}
