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

namespace System
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Diagnostics;
	using System.Globalization;
	using System.Runtime.CompilerServices;
	using System.Security.Cryptography;
	using Doxense.Diagnostics.Contracts;
	using JetBrains.Annotations;

	/// <summary>Represents a 64-bit UUID that is stored in high-endian format on the wire</summary>
	[DebuggerDisplay("[{ToString(),nq}]")]
	[ImmutableObject(true), PublicAPI, Serializable]
	public readonly struct Uuid64 : IEquatable<Uuid64>, IComparable<Uuid64>, ISpanFormattable
#if NET8_0_OR_GREATER
		, ISpanParsable<Uuid64>
#endif
	{

		/// <summary>Uuid64 with all bits set to zero: <c>00000000-00000000</c></summary>
		public static readonly Uuid64 Empty;

		/// <summary>Uuid164 with all bits set to one: <c>FFFFFFFF-FFFFFFFF</c></summary>
		public static readonly Uuid64 MaxValue = new(ulong.MaxValue);

		/// <summary>Size is 8 bytes</summary>
		public const int SizeOf = 8;

		private readonly ulong m_value;
		//note: this will be in host order (so probably Little-Endian) in order to simplify parsing and ordering

		#region Constructors...

		/// <summary>Creates a new <see cref="Uuid64"/> from a 64-bit unsigned integer</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Uuid64(ulong value) => m_value = value;

		/// <summary>Creates a new <see cref="Uuid64"/> from a 64-bit signed integer</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Uuid64(long value) => m_value = unchecked((ulong) value);

		/// <summary>Creates a new <see cref="Uuid64"/> from two 32-bits components</summary>
		/// <param name="a">Upper 32 bits (<c>XXXXXXXX-........</c>)</param>
		/// <param name="b">Lower 32 bits (<c>........-XXXXXXXX</c>)</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Uuid64(uint a, uint b)
		{
			m_value = ((ulong) a << 32) | b;
		}

		/// <summary>Creates a new <see cref="Uuid64"/> from fourt 16-bits components</summary>
		/// <param name="a">Upper 16 bits of the first part  (<c>XXXX....-........</c>)</param>
		/// <param name="b">Upper 16 bits of the first part  (<c>....XXXX-........</c>)</param>
		/// <param name="c">Upper 16 bits of the second part (<c>........-XXXX....</c>)</param>
		/// <param name="d">Lower 16 bits of the second part (<c>........-....XXXX</c>)</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Uuid64(ushort a, ushort b, ushort c, ushort d)
		{
			m_value = ((ulong) a << 48) | ((ulong) b << 32) | ((ulong) c << 16) | d;
		}

		/// <summary>Creates a new <see cref="Uuid64"/> from a 16-bit and 48-bits components</summary>
		/// <param name="a">Upper 16 bits (XXXX....-........)</param>
		/// <param name="b">Lower 48 bits (....XXXX-XXXXXXXX)</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Uuid64(ushort a, long b)
		{
			m_value = ((ulong) a << 48) | ((ulong) b & ((1UL << 48) - 1));
		}

		[Pure, MethodImpl(MethodImplOptions.NoInlining)]
		private static Exception FailInvalidBufferSize([InvokerParameterName] string arg) => ThrowHelper.ArgumentException(arg, "Value must be 8 bytes long");

		[Pure, MethodImpl(MethodImplOptions.NoInlining)]
		private static Exception FailInvalidFormat() => ThrowHelper.FormatException($"Invalid {nameof(Uuid64)} format");

		/// <summary>Generate a new random 64-bit UUID.</summary>
		/// <remarks>If you need sequential or cryptographic uuids, you should use a different generator.</remarks>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 NewUuid()
		{
			// we use Guid.NewGuid() as a source of ~128 bits of entropy
			var x = Guid.NewGuid();
			// and we fold both 64-bits parts into a single value
			ref ulong p = ref Unsafe.As<Guid, ulong>(ref x);
			ref ulong q = ref Unsafe.Add(ref p, 1);
			return new Uuid64(p ^ q);
		}

		/// <summary>Generates a new random 64-bit UUID, using the specified random number generator</summary>
		/// <param name="rng">Random number generator</param>
		/// <returns>A random <see cref="Uuid64"/> that is less than <see cref="Uuid64.MaxValue"/></returns>
		public static Uuid64 Random(Random rng) => new (rng.NextInt64(long.MinValue, long.MaxValue));

		/// <summary>Generates a new random 64-bit UUID, using the specified random number generator</summary>
		/// <param name="rng">Random number generator</param>
		/// <param name="minValue">The inclusive lower bound of the random UUID to be generated.</param>
		/// <param name="maxValue">The exclusive uppper bound of the random UUID to be generated.</param>
		/// <returns>A random <see cref="Uuid64"/> that is greater than or equal to <paramref name="minValue"/> and less than <paramref name="maxValue"/></returns>
		public static Uuid64 Random(Random rng, Uuid64 minValue, Uuid64 maxValue) => Random(rng, minValue.m_value, maxValue.m_value);

		/// <summary>Generates a new random 64-bit UUID, using the specified random number generator</summary>
		/// <param name="rng">Random number generator</param>
		/// <param name="minValue">The inclusive lower bound of the random UUID to be generated.</param>
		/// <param name="maxValue">The exclusive uppper bound of the random UUID to be generated.</param>
		/// <returns>A random <see cref="Uuid64"/> that is greater than or equal to <paramref name="minValue"/> and less than <paramref name="maxValue"/></returns>
		public static Uuid64 Random(Random rng, ulong minValue, ulong maxValue)
		{
			const ulong MAX_RANGE = 0x8000000000000000uL;
			var range = checked(maxValue - minValue);
			if (range < MAX_RANGE)
			{
				ulong x = (ulong) rng.NextInt64(0, (long) range);
				x += minValue;
				return new Uuid64(x);
			}
			else
			{
				ulong x = (ulong) rng.NextInt64((long) (range - MAX_RANGE));
				x += minValue;
				x += MAX_RANGE;
				return new(x);
			}
		}

		/// <summary>Generates a new random 64-bit UUID, using the specified random number generator</summary>
		/// <param name="rng">Random number generator</param>
		/// <param name="maxValue">The exclusive uppper bound of the random UUID to be generated.</param>
		/// <returns>A random <see cref="Uuid64"/> that is less than <paramref name="maxValue"/></returns>
		public static Uuid64 Random(Random rng, Uuid64 maxValue) => Random(rng, maxValue.m_value);

		/// <summary>Generates a new random 64-bit UUID, using the specified random number generator</summary>
		/// <param name="rng">Random number generator</param>
		/// <param name="maxValue">The exclusive uppper bound of the random UUID to be generated.</param>
		/// <returns>A random <see cref="Uuid64"/> that is less than <paramref name="maxValue"/></returns>
		public static Uuid64 Random(Random rng, ulong maxValue)
		{
			return maxValue switch
			{
				<= long.MaxValue => new Uuid64(rng.NextInt64(0, (long) maxValue)),
				ulong.MaxValue => new Uuid64(rng.NextInt64(long.MinValue, long.MaxValue)),
				_ => RandomLargeRange(rng, maxValue)
			};

			static Uuid64 RandomLargeRange(Random rng, ulong maxValue)
			{
				// move the range with 0 starting at long.MinValue
				long upper = checked((long) (maxValue - 9223372036854775808));
				long x = rng.NextInt64(long.MinValue, upper);
				if (x <= 0)
				{
					return new Uuid64(-x);
				}

				ulong shifted = 9223372036854775807UL + (ulong) x;
				return new Uuid64(shifted);
			}
		}

		#endregion

		#region Decomposition...

		/// <summary>Split into two 32-bit halves</summary>
		/// <param name="a">xxxxxxxx-........</param>
		/// <param name="b">........-xxxxxxxx</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Deconstruct(out uint a, out uint b)
		{
			ulong value = m_value;
			a = (uint) (value >> 32);
			b = (uint) value;
		}

		/// <summary>Split into two halves</summary>
		/// <param name="a">xxxx....-........</param>
		/// <param name="b">....xxxx-........</param>
		/// <param name="c">........-xxxx....</param>
		/// <param name="d">........-....xxxx</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Deconstruct(out ushort a, out ushort b, out ushort c, out ushort d)
		{
			ulong value = m_value;
			a = (ushort) (value >> 48);
			b = (ushort) (value >> 32);
			c = (ushort) (value >> 16);
			d = (ushort) value;
		}

		#endregion

		#region Reading...

		/// <summary>Read a 64-bit UUID from a byte array</summary>
		/// <param name="value">Array of exactly 0 or 8 bytes</param>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 Read(byte[] value)
		{
			return Read(value.AsSpan());
		}

		/// <summary>Read a 64-bit UUID from slice of memory</summary>
		/// <param name="value">slice of exactly 0 or 8 bytes</param>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 Read(Slice value)
		{
			return Read(value.Span);
		}

		/// <summary>Read a 64-bit UUID from slice of memory</summary>
		/// <param name="value">Span of exactly 0 or 8 bytes</param>
		[Pure]
		public static Uuid64 Read(ReadOnlySpan<byte> value)
		{
			if (value.Length == 0) return default;
			if (value.Length == 8) return new Uuid64(BinaryPrimitives.ReadInt64BigEndian(value));
			throw FailInvalidBufferSize(nameof(value));
		}

		#endregion

		#region Parsing...

		/// <summary>Parses a string into a <see cref="Uuid64"/></summary>
		/// <param name="input">The string to parse.</param>
		/// <param name="provider">This argument is ignored.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="input" /> is <see langword="null" />.</exception>
		/// <exception cref="T:System.FormatException"><paramref name="input" /> is not in the correct format.</exception>
		/// <exception cref="T:System.OverflowException"><paramref name="input" /> is not representable by a <see cref="Uuid128" />.</exception>
		/// <returns>The result of parsing <paramref name="input" />.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 Parse(string input, IFormatProvider? provider = null)
		{
			Contract.NotNull(input);
			return TryParse(input.AsSpan(), out var value) ? value : throw FailInvalidFormat();
		}

		/// <summary>Parses a string into a <see cref="Uuid128"/></summary>
		/// <param name="input">The string to parse.</param>
		/// <param name="provider">This argument is ignored.</param>
		/// <exception cref="T:System.FormatException"><paramref name="input" /> is not in the correct format.</exception>
		/// <exception cref="T:System.OverflowException"><paramref name="input" /> is not representable by a <see cref="Uuid128" />.</exception>
		/// <returns>The result of parsing <paramref name="input" />.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 Parse(ReadOnlySpan<char> input, IFormatProvider? provider = null) => TryParse(input, out var value) ? value : throw FailInvalidFormat();

		/// <summary>Parse a Base62 encoded string representation of an UUid64</summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Uuid64 FromBase62(string buffer)
		{
			Contract.NotNull(buffer);
			return TryParseBase62(buffer.AsSpan(), out var value) ? value : throw FailInvalidFormat();
		}

		/// <summary>Tries to parse a string into a <see cref="Uuid64"/></summary>
		/// <param name="input">The string to parse.</param>
		/// <param name="result">When this method returns, contains the result of successfully parsing <paramref name="input" />, or an undefined value on failure.</param>
		/// <returns> <see langword="true" /> if <paramref name="input" /> was successfully parsed; otherwise, <see langword="false" />.</returns>
		public static bool TryParse(string? input, out Uuid64 result)
		{
			if (input == null)
			{
				result = default;
				return false;
			}
			return TryParse(input.AsSpan(), out result);
		}

		/// <summary>Tries to parse a string into a <see cref="Uuid64"/></summary>
		/// <param name="input">The string to parse.</param>
		/// <param name="provider">This argument is ignored.</param>
		/// <param name="result">When this method returns, contains the result of successfully parsing <paramref name="input" />, or an undefined value on failure.</param>
		/// <returns> <see langword="true" /> if <paramref name="input" /> was successfully parsed; otherwise, <see langword="false" />.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public static bool TryParse(string? input, IFormatProvider? provider, out Uuid64 result)
			=> TryParse(input, out result);

		/// <summary>Tries to parse a span of characters into a <see cref="Uuid64"/></summary>
		/// <param name="input">The span of characters to parse.</param>
		/// <param name="provider">This argument is ignored.</param>
		/// <param name="result">When this method returns, contains the result of successfully parsing <paramref name="input" />, or an undefined value on failure.</param>
		/// <returns> <see langword="true" /> if <paramref name="input" /> was successfully parsed; otherwise, <see langword="false" />.</returns>
		public static bool TryParse(ReadOnlySpan<char> input, IFormatProvider? provider, out Uuid64 result)
			=> TryParse(input, out result);

		/// <summary>Tries to parse a span of characters into a <see cref="Uuid64"/></summary>
		/// <param name="input">The span of characters to parse.</param>
		/// <param name="result">When this method returns, contains the result of successfully parsing <paramref name="input" />, or an undefined value on failure.</param>
		/// <returns> <see langword="true" /> if <paramref name="input" /> was successfully parsed; otherwise, <see langword="false" />.</returns>
		public static bool TryParse(ReadOnlySpan<char> input, out Uuid64 result)
		{
			// we support the following formats: "{hex8-hex8}", "{hex16}", "hex8-hex8", "hex16" and "base62"
			// we don't support base10 format, because there is no way to differentiate from hex or base62

			switch (input.Length)
			{
				case 0:
				{ // empty
					result = default;
					return true;
				}
				case 16:
				{ // xxxxxxxxxxxxxxxx
					return TryDecode16Unsafe(input, separator: false, out result);
				}
				case 17:
				{ // xxxxxxxx-xxxxxxxx
					if (input[8] != '-')
					{
						result = default;
						return false;
					}

					return TryDecode16Unsafe(input, separator: true, out result);
				}
				case 18:
				{ // {xxxxxxxxxxxxxxxx}
					if (input[0] != '{' || input[17] != '}')
					{
						result = default;
						return false;
					}
					return TryDecode16Unsafe(input[1..^1], separator: false, out result);
				}
				case 19:
				{ // {xxxxxxxx-xxxxxxxx}
					if (input[0] != '{' || input[18] != '}')
					{
						result = default;
						return false;
					}
					return TryDecode16Unsafe(input[1..^1], separator: true, out result);
				}
				default:
				{
					result = default;
					return false;
				}
			}
		}

		public static bool TryParseBase62(string? input, out Uuid64 result)
		{
			if (input == null)
			{
				result = default;
				return false;
			}
			return TryParseBase62(input.AsSpan(), out result);
		}

		public static bool TryParseBase62(ReadOnlySpan<char> input, out Uuid64 result)
		{
			if (input.Length == 0)
			{
				result = default;
				return true;
			}

			if (input.Length <= 11 && Base62.TryDecode(input, out ulong x))
			{
				result = new Uuid64(x);
				return true;
			}

			result = default;
			return false;
		}

		#endregion

		#region Casting...

		//note: we cannot use implicit casting because it causes too many conflicts with "out" parameters and Desconstruction

		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static explicit operator Uuid64(ulong value)
		{
			return new Uuid64(value);
		}

		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static explicit operator ulong(Uuid64 value)
		{
			return value.m_value;
		}

		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static explicit operator Uuid64(long value)
		{
			return new Uuid64(value);
		}

		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static explicit operator long(Uuid64 value)
		{
			return (long) value.m_value;
		}

		#endregion

		#region IFormattable...

		/// <summary>Returns the equivalent 64-bit signed integer</summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long ToInt64() => unchecked((long) m_value);

		/// <summary>Returns the equivalent 64-bit unsigned integer</summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ulong ToUInt64() => m_value;

		/// <summary>Converts this instance into a <see cref="Slice"/></summary>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Slice ToSlice() => Slice.FromFixedU64BE(m_value);

		/// <summary>Converts this instance into a byte array</summary>
		[Pure]
		public byte[] ToByteArray()
		{
			var bytes = Slice.FromFixedU64BE(m_value).Array;
			Contract.Debug.Ensures(bytes != null && bytes.Length == 8); // HACKHACK: for perf reasons, we rely on the fact that Slice.FromFixedU64BE() allocates a new 8-byte array that we can return without copying
			return bytes;
		}

		/// <summary>Returns a string representation of the value of this instance.</summary>
		/// <returns>String using the format "xxxxxxxx-xxxxxxxx", where 'x' is a lower-case hexadecimal digit</returns>
		/// <remarks>Strings returned by this method will always to 17 characters long.</remarks>
		public override string ToString()
		{
			return ToString("D", null);
		}

		/// <summary>Returns a string representation of the value of this <see cref="Uuid64"/> instance, according to the provided format specifier.</summary>
		/// <param name="format">A single format specifier that indicates how to format the value of this Guid. The format parameter can be "D", "B", "X", "G", "Z" or "N". If format is null or an empty string (""), "D" is used.</param>
		/// <returns>The value of this <see cref="Uuid64"/>, using the specified format.</returns>
		/// <remarks>See <see cref="ToString(string, IFormatProvider)"/> for a description of the different formats</remarks>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public string ToString(string? format)
		{
			return ToString(format, null);
		}

		/// <summary>Returns a string representation of the value of this instance of the <see cref="Uuid64"/> class, according to the provided format specifier and culture-specific format information.</summary>
		/// <param name="format">A single format specifier that indicates how to format the value of this Guid. The format parameter can be "D", "N", "Z", "R", "X" or "B". If format is null or an empty string (""), "D" is used.</param>
		/// <param name="formatProvider">An object that supplies culture-specific formatting information. Only used for the "R" format.</param>
		/// <returns>The value of this <see cref="Uuid64"/>, using the specified format.</returns>
		/// <example>
		/// <p>The <b>D</b> format encodes the value as two groups of 8 hexadecimal digits, separated by an hyphen: "01234567-89abcdef" (17 characters).</p>
		/// <p>The <b>X</b> format encodes the value as a single group of 16 hexadecimal digits: "0123456789abcdef" (16 characters).</p>
		/// <p>The <b>B</b> format is equivalent to the <b>D</b> format, but surrounded with '{' and '}': "{01234567-89abcdef}" (19 characters).</p>
		/// <p>The <b>R</b> format encodes the value as a decimal number "1234567890" (1 to 20 characters) which can be parsed as an UInt64 without loss.</p>
		/// <p>The <b>C</b> format uses a compact base-62 encoding that preserves lexicographical ordering, composed of digits, uppercase alpha and lowercase alpha, suitable for compact representation that can fit in a querystring.</p>
		/// <p>The <b>Z</b> format is equivalent to the <b>C</b> format, but with extra padding so that the string is always 11 characters long.</p>
		/// </example>
		public string ToString(string? format, IFormatProvider? formatProvider)
		{
			switch(format)
			{
				case null or "" or "D":
				{ // Default format is "XXXXXXXX-XXXXXXXX"
					return EncodeTwoParts(m_value, separator: '-', quotes: false, upper: true);
				}
				case "d":
				{ // Default format is "xxxxxxxx-xxxxxxxx"
					return EncodeTwoParts(m_value, separator: '-', quotes: false, upper: false);
				}

				case "C":
				case "c":
				{ // base 62, compact, no padding
					return Base62.Encode(m_value, padded: false);
				}
				case "Z":
				case "z":
				{ // base 62, padded with '0' up to 11 chars
					return Base62.Encode(m_value, padded: true);
				}

				case "R":
				case "r":
				{ // Integer: "1234567890"
					return m_value.ToString(null, formatProvider ?? CultureInfo.InvariantCulture);
				}

				case "X": //TODO: Guid.ToString("X") returns "{0x.....,0x.....,...}"
				case "N":
				{ // "XXXXXXXXXXXXXXXX"
					return EncodeOnePart(m_value, quotes: false, upper: true);
				}
				case "x": //TODO: Guid.ToString("X") returns "{0x.....,0x.....,...}"
				case "n":
				{ // "xxxxxxxxxxxxxxxx"
					return EncodeOnePart(m_value, quotes: false, upper: false);
				}

				case "B":
				{ // "{XXXXXXXX-XXXXXXXX}"
					return EncodeTwoParts(m_value, separator: '-', quotes: true, upper: true);
				}
				case "b":
				{ // "{xxxxxxxx-xxxxxxxx}"
					return EncodeTwoParts(m_value, separator: '-', quotes: true, upper: false);
				}

				case "V":
				{ // "XX-XX-XX-XX-XX-XX-XX-XX"
					return EncodeEightParts(m_value, separator: '-', quotes: false, upper: true);
				}
				case "v":
				{ // "xx-xx-xx-xx-xx-xx-xx-xx"
					return EncodeEightParts(m_value, separator: '-', quotes: false, upper: false);
				}

				case "M":
				{ // "XX:XX:XX:XX:XX:XX:XX:XX"
					return EncodeEightParts(m_value, separator: ':', quotes: false, upper: true);
				}
				case "m":
				{ // "xx:xx:xx:xx:xx:xx:xx:xx"
					return EncodeEightParts(m_value, separator: ':', quotes: false, upper: false);
				}

				default:
				{
					throw new FormatException("Invalid " + nameof(Uuid64) + " format specification.");
				}
			}
		}

		/// <summary>Tries to format the value of the current instance into the provided span of characters.</summary>
		/// <param name="destination">The span in which to write this instance's value formatted as a span of characters.</param>
		/// <param name="charsWritten">When this method returns, contains the number of characters that were written in <paramref name="destination" />.</param>
		/// <param name="format">A span containing the characters that represent a standard or custom format string that defines the acceptable format for <paramref name="destination" />.</param>
		/// <param name="provider">An optional object that supplies culture-specific formatting information for <paramref name="destination" />.</param>
		/// <returns>
		/// <see langword="true" /> if the formatting was successful; otherwise, <see langword="false" />.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TryFormat(
			Span<char> destination,
			out int charsWritten,
			ReadOnlySpan<char> format,
			IFormatProvider? provider = null
		)
		{
			//TODO: BUGBUG: OPTIMIZE: this should be changed to not allocate memory!

			string s;
			switch(format)
			{
				case "" or "D":
				{ // Default format is "XXXXXXXX-XXXXXXXX"
					s = EncodeTwoParts(m_value, separator: '-', quotes: false, upper: true);
					break;
				}
				case "d":
				{ // Default format is "xxxxxxxx-xxxxxxxx"
					s = EncodeTwoParts(m_value, separator: '-', quotes: false, upper: false);
					break;
				}

				case "C":
				case "c":
				{ // base 62, compact, no padding
					s = Base62.Encode(m_value, padded: false);
					break;
				}
				case "Z":
				case "z":
				{ // base 62, padded with '0' up to 11 chars
					s = Base62.Encode(m_value, padded: true);
					break;
				}

				case "R":
				case "r":
				{ // Integer: "1234567890"
					s = m_value.ToString(null, provider ?? CultureInfo.InvariantCulture);
					break;
				}

				case "X": //TODO: Guid.ToString("X") returns "{0x.....,0x.....,...}"
				case "N":
				{ // "XXXXXXXXXXXXXXXX"
					s = EncodeOnePart(m_value, quotes: false, upper: true);
					break;
				}
				case "x": //TODO: Guid.ToString("X") returns "{0x.....,0x.....,...}"
				case "n":
				{ // "xxxxxxxxxxxxxxxx"
					s = EncodeOnePart(m_value, quotes: false, upper: false);
					break;
				}

				case "B":
				{ // "{XXXXXXXX-XXXXXXXX}"
					s = EncodeTwoParts(m_value, separator: '-', quotes: true, upper: true);
					break;
				}
				case "b":
				{ // "{xxxxxxxx-xxxxxxxx}"
					s = EncodeTwoParts(m_value, separator: '-', quotes: true, upper: false);
					break;
				}

				case "V":
				{ // "XX-XX-XX-XX-XX-XX-XX-XX"
					s = EncodeEightParts(m_value, separator: '-', quotes: false, upper: true);
					break;
				}
				case "v":
				{ // "xx-xx-xx-xx-xx-xx-xx-xx"
					s = EncodeEightParts(m_value, separator: '-', quotes: false, upper: false);
					break;
				}

				case "M":
				{ // "XX:XX:XX:XX:XX:XX:XX:XX"
					s = EncodeEightParts(m_value, separator: ':', quotes: false, upper: true);
					break;
				}
				case "m":
				{ // "xx:xx:xx:xx:xx:xx:xx:xx"
					s = EncodeEightParts(m_value, separator: ':', quotes: false, upper: false);
					break;
				}
				default:
				{
					throw new FormatException("Invalid " + nameof(Uuid64) + " format specification.");
				}
			}

			if (s.Length > destination.Length)
			{
				charsWritten = 0;
				return false;
			}

			s.CopyTo(destination);
			charsWritten = s.Length;
			return true;
		}

		#endregion

		#region IEquatable / IComparable...

		public override bool Equals(object? obj)
		{
			switch (obj)
			{
				case Uuid64 u64: return Equals(u64);
				case ulong ul: return m_value == ul;
				case long l: return m_value == (ulong) l;
				//TODO: string format ? Slice ?
			}
			return false;
		}

		public override int GetHashCode()
		{
			return ((int) m_value) ^ (int) (m_value >> 32);
		}

		public bool Equals(Uuid64 other)
		{
			return m_value == other.m_value;
		}

		public int CompareTo(Uuid64 other)
		{
			return m_value.CompareTo(other.m_value);
		}

		#endregion

		#region Base16 encoding...

		[Pure]
		private static char HexToLowerChar(int a)
		{
			a &= 0xF;
			return a > 9 ? (char)(a - 10 + 'a') : (char)(a + '0');
		}

		private static unsafe char* Hex8ToLowerChars(char* ptr, byte a)
		{
			Contract.Debug.Requires(ptr != null);
			ptr[0] = HexToLowerChar(a >> 4);
			ptr[1] = HexToLowerChar(a);
			return ptr + 2;
		}

		private static unsafe char* Hex32ToLowerChars(char* ptr, int a)
		{
			Contract.Debug.Requires(ptr != null);
			ptr[0] = HexToLowerChar(a >> 28);
			ptr[1] = HexToLowerChar(a >> 24);
			ptr[2] = HexToLowerChar(a >> 20);
			ptr[3] = HexToLowerChar(a >> 16);
			ptr[4] = HexToLowerChar(a >> 12);
			ptr[5] = HexToLowerChar(a >> 8);
			ptr[6] = HexToLowerChar(a >> 4);
			ptr[7] = HexToLowerChar(a);
			return ptr + 8;
		}

		[Pure]
		private static char HexToUpperChar(int a)
		{
			a &= 0xF;
			return a > 9 ? (char)(a - 10 + 'A') : (char)(a + '0');
		}

		private static unsafe char* Hex8ToUpperChars(char* ptr, int a)
		{
			Contract.Debug.Requires(ptr != null);
			ptr[0] = HexToUpperChar(a >> 4);
			ptr[1] = HexToUpperChar(a);
			return ptr + 2;
		}

		private static unsafe char* Hex32ToUpperChars(char* ptr, int a)
		{
			Contract.Debug.Requires(ptr != null);
			ptr[0] = HexToUpperChar(a >> 28);
			ptr[1] = HexToUpperChar(a >> 24);
			ptr[2] = HexToUpperChar(a >> 20);
			ptr[3] = HexToUpperChar(a >> 16);
			ptr[4] = HexToUpperChar(a >> 12);
			ptr[5] = HexToUpperChar(a >> 8);
			ptr[6] = HexToUpperChar(a >> 4);
			ptr[7] = HexToUpperChar(a);
			return ptr + 8;
		}

		[Pure]
		private static unsafe string EncodeOnePart(ulong value, bool quotes, bool upper)
		{
			int size = 16 + (quotes ? 2 : 0);
			char* buffer = stackalloc char[24]; // max 18 but round up to 24

			char* ptr = buffer;
			if (quotes) *ptr++ = '{';
			if (upper)
			{
				ptr = Hex32ToUpperChars(ptr, (int) (value >> 32));
				ptr = Hex32ToUpperChars(ptr, (int) (value & 0xFFFFFFFF));
			}
			else
			{
				ptr = Hex32ToLowerChars(ptr, (int) (value >> 32));
				ptr = Hex32ToLowerChars(ptr, (int) (value & 0xFFFFFFFF));
			}
			if (quotes) *ptr++ = '}';

			Contract.Debug.Ensures(ptr == buffer + size);
			return new string(buffer, 0, size);
		}

		[Pure]
		private static unsafe string EncodeTwoParts(ulong value, char separator, bool quotes, bool upper)
		{
			int size = 16 + 1 + (quotes ? 2 : 0);
			char* buffer = stackalloc char[24]; // max 19 but round up to 24

			char* ptr = buffer;
			if (quotes) *ptr++ = '{';
			if (upper)
			{
				ptr = Hex32ToUpperChars(ptr, (int) (value >> 32));
				*ptr++ = separator;
				ptr = Hex32ToUpperChars(ptr, (int) (value & 0xFFFFFFFF));
			}
			else
			{
				ptr = Hex32ToLowerChars(ptr, (int) (value >> 32));
				*ptr++ = separator;
				ptr = Hex32ToLowerChars(ptr, (int) (value & 0xFFFFFFFF));
			}
			if (quotes) *ptr++ = '}';

			Contract.Debug.Ensures(ptr == buffer + size);
			return new string(buffer, 0, size);
		}

		[Pure]
		private static unsafe string EncodeEightParts(ulong value, char separator, bool quotes, bool upper)
		{
			int size = 16 + 7 + (quotes ? 2 : 0);
			char* buffer = stackalloc char[32]; // max 25 but round up to 32

			char* ptr = buffer;
			if (quotes) *ptr++ = '{';
			if (upper)
			{
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 56));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 48));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 40));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 32));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 24));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 16));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) (value >> 8));
				*ptr++ = separator;
				ptr = Hex8ToUpperChars(ptr, (byte) value);
			}
			else
			{
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 56));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 48));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 40));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 32));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 24));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 16));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) (value >> 8));
				*ptr++ = separator;
				ptr = Hex8ToLowerChars(ptr, (byte) value);
			}
			if (quotes) *ptr++ = '}';

			Contract.Debug.Ensures(ptr == buffer + size);
			return new string(buffer, 0, size);
		}

		private const int INVALID_CHAR = -1;

		[Pure]
		private static int CharToHex(char c)
		{
			if (c <= '9')
			{
				return c >= '0' ? (c - 48) : INVALID_CHAR;
			}
			if (c <= 'F')
			{
				return c >= 'A' ? (c - 55) : INVALID_CHAR;
			}
			if (c <= 'f')
			{
				return c >= 'a' ? (c - 87) : INVALID_CHAR;
			}
			return INVALID_CHAR;
		}

		private static bool TryCharsToHexsUnsafe(ReadOnlySpan<char> chars, out uint result)
		{
			int word = 0;
			for (int i = 0; i < 8; i++)
			{
				int a = CharToHex(chars[i]);
				if (a == INVALID_CHAR)
				{
					result = 0;
					return false;
				}
				word = (word << 4) | a;
			}
			result = (uint)word;
			return true;
		}

		private static bool TryDecode16Unsafe(ReadOnlySpan<char> chars, bool separator, out Uuid64 result)
		{
			if ((!separator || chars[8] == '-')
			 && TryCharsToHexsUnsafe(chars, out uint hi)
			 && TryCharsToHexsUnsafe(chars.Slice(separator ? 9 : 8), out uint lo))
			{
				result = new Uuid64(((ulong)hi << 32) | lo);
				return true;
			}
			result = default(Uuid64);
			return false;
		}

		#endregion

		#region Base62 encoding...

		//NOTE: this version of base62 encoding puts the digits BEFORE the letters, to ensure that the string representation of a UUID64 is in the same order as its byte[] or ulong version.
		// => This scheme use the "0-9A-Za-z" ordering, while most other base62 encoder use "a-zA-Z0-9"

		private static class Base62
		{
			//note: nested static class, so that we only allocate the internal buffers if Base62 encoding is actually used

			private static readonly char[] Base62LexicographicChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

			private static readonly int[] Base62Values = new int[3 * 32]
			{
				/* 32.. 63 */ -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, -1, -1, -1, -1, -1, -1,
				/* 64.. 95 */ -1, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, -1, -1, -1, -1, -1,
				/* 96..127 */ -1, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, -1, -1, -1, -1, -1,
			};

			/// <summary>Encode a 64-bit value into a base-62 string</summary>
			/// <param name="value">64-bit value to encode</param>
			/// <param name="padded">If true, keep the leading '0' to return a string of length 11. If false, discards all extra leading '0' digits.</param>
			/// <returns>String that contains only digits, lower and upper case letters. The string will be lexicographically ordered, which means that sorting by string will give the same order as sorting by value.</returns>
			/// <sample>
			/// Encode62(0, false) => "0"
			/// Encode62(0, true) => "00000000000"
			/// Encode62(0xDEADBEEF) => ""
			/// </sample>
			public static string Encode(ulong value, bool padded)
			{
				// special case for default(Uuid64) which may be more frequent than others
				if (value == 0) return padded ? "00000000000" : "0";

				// encoding a 64 bits value in Base62 yields 10.75 "digits", which is rounded up to 11 chars.
				const int MAX_SIZE = 11;

				unsafe
				{
					// The maximum size is 11 chars, but we will allocate 64 bytes on the stack to keep alignment.
					char* chars = stackalloc char[16];
					char[] bc = Base62LexicographicChars;

					// start from the last "digit"
					char* pc = chars + (MAX_SIZE - 1);

					while (pc >= chars)
					{
						ulong r = value % 62L;
						value /= 62L;
						*pc-- = bc[(int) r];
						if (!padded && value == 0)
						{ // the rest will be all zeroes
							break;
						}
					}

					++pc;
					int count = MAX_SIZE - (int) (pc - chars);
					Contract.Debug.Assert(count > 0 && count <= 11);
					return count <= 0 ? string.Empty : new string(pc, 0, count);
				}
			}

			public static bool TryDecode(ReadOnlySpan<char> s, out ulong value)
			{
				if (s == null || s.Length == 0 || s.Length > 11)
				{ // fail: too small/too big
					value = 0;
					return false;
				}

				// we know that the original value is exactly 64bits, and any missing digit is '0'
				ulong factor = 1UL;
				ulong acc = 0UL;
				int p = s.Length - 1;
				int[] bv = Base62Values;
				while (p >= 0)
				{
					// read digit
					int a = s[p];
					// decode base62 digit
					a = a >= 32 && a < 128 ? bv[a - 32] : -1;
					if (a == -1)
					{ // fail: invalid character
						value = 0;
						return false;
					}
					// accumulate, while checking for overflow
					acc = checked(acc + ((ulong) a * factor));
					if (p-- > 0) factor *= 62;
				}
				value = acc;
				return true;
			}

		}

		#endregion

		#region Unsafe I/O...

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void WriteToUnsafe(Span<byte> buffer)
		{
			BinaryPrimitives.WriteUInt64BigEndian(buffer, m_value);
		}

		public void WriteTo(Span<byte> destination)
		{
			if (!BinaryPrimitives.TryWriteUInt64BigEndian(destination, m_value))
			{
				throw FailInvalidBufferSize(nameof(destination));
			}
		}

		public bool TryWriteTo(Span<byte> destination)
		{
			return BinaryPrimitives.TryWriteUInt64BigEndian(destination, m_value);
		}

		#endregion

		#region Operators...

		public static bool operator ==(Uuid64 left, Uuid64 right)
		{
			return left.m_value == right.m_value;
		}

		public static bool operator !=(Uuid64 left, Uuid64 right)
		{
			return left.m_value != right.m_value;
		}

		public static bool operator >(Uuid64 left, Uuid64 right)
		{
			return left.m_value > right.m_value;
		}

		public static bool operator >=(Uuid64 left, Uuid64 right)
		{
			return left.m_value >= right.m_value;
		}

		public static bool operator <(Uuid64 left, Uuid64 right)
		{
			return left.m_value < right.m_value;
		}

		public static bool operator <=(Uuid64 left, Uuid64 right)
		{
			return left.m_value <= right.m_value;
		}

		// Comparing an Uuid64 to a 64-bit integer can have sense for "if (id == 0)" or "if (id != 0)" ?

		public static bool operator ==(Uuid64 left, long right)
		{
			return left.m_value == (ulong)right;
		}

		public static bool operator ==(Uuid64 left, ulong right)
		{
			return left.m_value == right;
		}

		public static bool operator !=(Uuid64 left, long right)
		{
			return left.m_value != (ulong)right;
		}

		public static bool operator !=(Uuid64 left, ulong right)
		{
			return left.m_value != right;
		}

		/// <summary>Add a value from this instance</summary>
		public static Uuid64 operator +(Uuid64 left, long right)
		{
			//TODO: how to handle overflow ? negative values ?
			ulong v = (ulong)right;
			return new Uuid64(checked(left.m_value + v));
		}

		/// <summary>Add a value from this instance</summary>
		public static Uuid64 operator +(Uuid64 left, ulong right)
		{
			return new Uuid64(checked(left.m_value + right));
		}

		/// <summary>Subtract a value from this instance</summary>
		public static Uuid64 operator -(Uuid64 left, long right)
		{
			//TODO: how to handle overflow ? negative values ?
			ulong v = (ulong)right;
			return new Uuid64(checked(left.m_value - v));
		}

		/// <summary>Subtract a value from this instance</summary>
		public static Uuid64 operator -(Uuid64 left, ulong right)
		{
			return new Uuid64(checked(left.m_value - right));
		}

		/// <summary>Increments the value of this instance</summary>
		public static Uuid64 operator ++(Uuid64 value)
		{
			return new Uuid64(checked(value.m_value + 1));
		}

		/// <summary>Decrements the value of this instance</summary>
		public static Uuid64 operator --(Uuid64 value)
		{
			return new Uuid64(checked(value.m_value - 1));
		}

		#endregion

		/// <summary>Instance of this times can be used to test Uuid64 for equality and ordering</summary>
		public sealed class Comparer : IEqualityComparer<Uuid64>, IComparer<Uuid64>
		{

			public static readonly Comparer Default = new Comparer();

			private Comparer()
			{ }

			public bool Equals(Uuid64 x, Uuid64 y)
			{
				return x.m_value == y.m_value;
			}

			public int GetHashCode(Uuid64 obj)
			{
				return obj.m_value.GetHashCode();
			}

			public int Compare(Uuid64 x, Uuid64 y)
			{
				return x.m_value.CompareTo(y.m_value);
			}
		}

		/// <summary>Generates 64-bit UUIDs using a secure random number generator</summary>
		/// <remarks>Methods of this type are thread-safe.</remarks>
		[PublicAPI]
		public sealed class RandomGenerator
		{

			/// <summary>Default instance of a random generator</summary>
			/// <remarks>Using this instance will introduce a global lock in your application. You can create specific instances for worker threads, if you require concurrency.</remarks>
			public static readonly Uuid64.RandomGenerator Default = new Uuid64.RandomGenerator();

			private RandomNumberGenerator Rng { get; }

			private readonly byte[] Scratch = new byte[SizeOf];

			/// <summary>Create a new instance of a random UUID generator</summary>
			public RandomGenerator()
				: this(null)
			{ }

			/// <summary>Create a new instance of a random UUID generator, using a specific random number generator</summary>
			public RandomGenerator(RandomNumberGenerator? generator)
			{
				this.Rng = generator ?? RandomNumberGenerator.Create();
			}

			/// <summary>Return a new random 64-bit UUID</summary>
			/// <returns>Uuid64 that contains 64 bits worth of randomness.</returns>
			/// <remarks>
			/// <p>This methods needs to acquire a lock. If multiple threads needs to generate ids concurrently, you may need to create an instance of this class for each threads.</p>
			/// <p>The uniqueness of the generated uuids depends on the quality of the random number generator. If you cannot tolerate collisions, you either have to check if a newly generated uid already exists, or use a different kind of generator.</p>
			/// </remarks>
			[Pure]
			// ReSharper disable once MemberHidesStaticFromOuterClass
			public Uuid64 NewUuid()
			{
				//REVIEW: OPTIMIZE: use a per-thread instance of the rng and scratch buffer?
				// => right now, NewUuid() is a Global Lock for the whole process!
				lock (this.Rng)
				{
					// get 8 bytes of randomness (0 allowed)
					this.Rng.GetBytes(this.Scratch);
					//note: do *NOT* call GetBytes(byte[], int, int) because it creates creates a temp buffer, calls GetBytes(byte[]) and copy the result back! (as of .NET 4.7.1)
					//TODO: PERF: use Span<byte> APIs once (if?) they become available!
					return Uuid64.Read(this.Scratch);
				}
			}

		}
	}

}
