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

namespace Doxense.Linq
{
	using System;
	using System.Buffers;
	using System.Diagnostics;
	using System.Globalization;
	using System.Runtime.CompilerServices;
	using Doxense.Serialization;
	using Doxense.Serialization.Json;

	/// <summary>Small buffer that keeps a list of chunks that are larger and larger</summary>
	[DebuggerDisplay("Count={Count}, Capacity{Buffer.Length}")]
	[DebuggerTypeProxy(typeof(ValueStringWriterDebugView))]
	[PublicAPI]
	public struct ValueStringWriter : IDisposable, IBufferWriter<char>
	{

		// This should only be used when needing to create a list of array of a few elements with as few memory allocations as possible,
		// either by passing a pre-allocated span (on the stack usually), or by using pooled buffers when a resize is required.
		
		// One typical use is to start from a small buffer allocated on the stack, that will be used until the buffer needs to be resized,
		// in which case another buffer will be used from a shared pool.

		/// <summary>Current buffer</summary>
		private char[] Buffer;

		/// <summary>Number of items in the buffer</summary>
		public int Count;

		public ValueStringWriter(int capacity = 0)
		{
			Contract.Positive(capacity);
			this.Count = 0;
			this.Buffer = capacity > 0 ? ArrayPool<char>.Shared.Rent(capacity) : [ ];
		}

		/// <summary>Returns a span with all the items already written to this buffer</summary>
		public Span<char> Span => this.Count > 0 ? this.Buffer.AsSpan(0, this.Count) : default;

		/// <summary>Returns a span with all the items already written to this buffer</summary>
		public Memory<char> Memory => this.Count > 0 ? this.Buffer.AsMemory(0, this.Count) : default;

		/// <summary>Returns a span with all the items already written to this buffer</summary>
		public ArraySegment<char> Segment => this.Count > 0 ? new ArraySegment<char>(this.Buffer, 0, this.Count) : default;

		/// <summary>Returns the current capacity of the buffer</summary>
		public int Capacity => this.Buffer.Length;

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public void Write(char item)
		{
			int pos = this.Count;
			var buff = this.Buffer;
			if ((uint) pos < (uint) buff.Length)
			{
				buff[pos] = item;
				this.Count = pos + 1;
			}
			else
			{
				AddWithResize(item);
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private void AddWithResize(char item)
		{
			int pos = this.Count;
			Grow(1);
			this.Buffer[pos] = item;
			this.Count = pos + 1;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public void Write(string items)
		{
			Write(items.AsSpan());
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
#if NET9_0_OR_GREATER
		[OverloadResolutionPriority(1)]
#endif
		public void Write(scoped ReadOnlySpan<char> items)
		{
			int pos = this.Count;
			var buf = this.Buffer;
			Contract.Debug.Assert(buf != null);
			if ((uint) (items.Length + this.Count) <= (uint) buf.Length)
			{
				items.CopyTo(buf.AsSpan(pos));
				this.Count = pos + items.Length;
			}
			else
			{
				AddWithResize(items);
			}
		}

		public void Write(char prefix, char value)
		{
			var span = Allocate(2);
			span[0] = prefix;
			span[1] = value;
		}

		public void Write(char prefix, string value)
		{
			var span = Allocate(checked(value.Length + 1));
			span[0] = prefix;
			value.CopyTo(span.Slice(1));
		}

		public void Write(char prefix, scoped ReadOnlySpan<char> value)
		{
			var span = Allocate(checked(value.Length + 1));
			span[0] = prefix;
			value.CopyTo(span.Slice(1));
		}

		public void Write(char prefix, char value, char suffix)
		{
			var span = Allocate(3);
			span[0] = prefix;
			span[1] = value;
			span[2] = suffix;
		}

		public void Write(char prefix, string value, char suffix)
		{
			var span = Allocate(checked(value.Length + 2));
			span[0] = prefix;
			value.CopyTo(span.Slice(1));
			span[^1] = suffix;
		}

		public void Write(char prefix, scoped ReadOnlySpan<char> value, char suffix)
		{
			var span = Allocate(checked(value.Length + 2));
			span[0] = prefix;
			value.CopyTo(span.Slice(1));
			span[^1] = suffix;
		}

		public void Write(string prefix, string value)
		{
			var span = Allocate(checked(prefix.Length + value.Length));
			prefix.CopyTo(span);
			value.CopyTo(span.Slice(prefix.Length));
		}

		public void Write(scoped ReadOnlySpan<char> prefix, scoped ReadOnlySpan<char> value)
		{
			var span = Allocate(checked(prefix.Length + value.Length));
			prefix.CopyTo(span);
			value.CopyTo(span.Slice(prefix.Length));
		}

		public void Write(char prefix, string value, string suffix)
		{
			var span = Allocate(checked(1 + value.Length + suffix.Length));
			span[0] = prefix;
			value.CopyTo(span.Slice(1));
			suffix.CopyTo(span.Slice(1 + value.Length));
		}

		public void Write(string prefix, string value, string suffix)
		{
			var span = Allocate(checked(prefix.Length + value.Length + suffix.Length));
			prefix.CopyTo(span);
			value.CopyTo(span.Slice(prefix.Length));
			suffix.CopyTo(span.Slice(prefix.Length + value.Length));
		}

		public void Write(string prefix, string value, char suffix)
		{
			var span = Allocate(checked(prefix.Length + value.Length + 1));
			prefix.CopyTo(span);
			value.CopyTo(span.Slice(prefix.Length));
			span[^1] = suffix;
		}

		public void Write(char c, int count)
		{
			var span = Allocate(count).Slice(0, count);
			Contract.Debug.Assert(span.Length == count);
			span.Fill(c);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private void AddWithResize(scoped ReadOnlySpan<char> items)
		{
			Grow(items.Length);
			int pos = this.Count;
			items.CopyTo(this.Buffer.AsSpan(pos));
			this.Count = pos + items.Length;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private void Grow(int required)
		{
			Contract.GreaterThan(required, 0);
			// Growth rate:
			// - first chunk size is 4 if empty
			// - double the buffer size
			// - except the first chunk who is set to the initial capacity

			const long MAX_CAPACITY = int.MaxValue & ~63;

			int length = this.Buffer.Length;
			long capacity = (long) length + required;
			if (capacity > MAX_CAPACITY)
			{
				throw new InvalidOperationException($"Buffer cannot expand because it would exceed the maximum size limit ({capacity:N0} > {MAX_CAPACITY:N0}).");
			}
			capacity = Math.Max(length != 0 ? length * 2 : 4, length + required);

			// allocate a new buffer (note: may be bigger than requested)
			var tmp = ArrayPool<char>.Shared.Rent((int) capacity);
			this.Buffer.AsSpan().CopyTo(tmp);

			var array = this.Buffer;
			this.Buffer = tmp;

			// return any previous buffer to the pool
			if (array.Length != 0)
			{
				array.AsSpan(0, this.Count).Clear();
				ArrayPool<char>.Shared.Return(array);
			}
		}

		private void Clear(bool release)
		{
			this.Span.Clear();
			this.Count = 0;

			// return the array to the pool
			if (release)
			{
				var array = Buffer;
				this.Buffer = [ ];

				if (array.Length != 0)
				{
					ArrayPool<char>.Shared.Return(array);
				}
			}
		}

		/// <summary>Clears the content of the buffer</summary>
		/// <remarks>The buffer count is reset to zero, but the current backing store remains the same</remarks>
		public void Clear() => Clear(release: false);

		/// <summary>Returns the content of the buffer as an array</summary>
		/// <returns>Array of size <see cref="Count"/> containing all the items in this buffer</returns>
		[Pure, CollectionAccess(CollectionAccessType.Read)]
		public override string ToString() => this.Span.ToString();

		[Pure, CollectionAccess(CollectionAccessType.ModifyExistingContent)]
		public string ToStringAndClear()
		{
			var res = this.Span.ToString();
			Clear(release: true);
			return res;
		}

		public Slice ToUtf8Slice(ArrayPool<byte>? pool = null)
		{
			var enc = CrystalJson.Utf8NoBom;
			var span = this.Span;
			int size = enc.GetByteCount(span);
			var tmp = pool?.Rent(size) ?? new byte[size];
			var written = enc.GetBytes(span, tmp);
			return tmp.AsSlice(0, written);
		}

		public Slice ToUtf8SliceAndClear(ArrayPool<byte>? pool = null)
		{
			var res = ToUtf8Slice(pool);
			Clear(release: true);
			return res;
		}

		public SliceOwner ToUtf8SliceOwner(ArrayPool<byte>? pool = null)
		{
			pool ??= ArrayPool<byte>.Shared;
			var enc = CrystalJson.Utf8NoBom;
			var span = this.Span;
			int size = enc.GetByteCount(span);
			var tmp = pool.Rent(size);
			var written = enc.GetBytes(span, tmp);
			Clear(release: true);
			return new SliceOwner(tmp.AsSlice(0, written), pool);
		}

		/// <summary>Copies the content of the buffer into a destination span</summary>
		[CollectionAccess(CollectionAccessType.Read)]
		public int CopyTo(Span<char> destination)
		{
			if (this.Count < destination.Length)
			{
				throw new ArgumentException("Destination buffer is too small", nameof(destination));
			}
			this.Span.CopyTo(destination);
			return this.Count;
		}

		/// <summary>Copies the content of the buffer into a destination span, if it is large enough</summary>
		[CollectionAccess(CollectionAccessType.Read)]
		public bool TryCopyTo(Span<char> destination, out int written)
		{
			int count = this.Count;
			if (count < destination.Length)
			{
				written = 0;
				return false;
			}

			this.Span.TryCopyTo(destination);
			written = count;
			return true;
		}

		#region IReadOnlyList<T>...

		[Pure, CollectionAccess(CollectionAccessType.ModifyExistingContent)]
		public ref char this[int index]
		{
			[Pure, CollectionAccess(CollectionAccessType.Read)]
			get => ref this.Buffer.AsSpan(0, this.Count)[index];
			//note: the span will perform the bound-checking for us
		}

		public Span<char>.Enumerator GetEnumerator()
		{
			return this.Span.GetEnumerator();
		}

		#endregion

		#region IBufferWriter<T>...

		/// <summary>Allocate a fixed-size span, and advance the cursor</summary>
		/// <param name="exactSize">Size of the buffer to allocate</param>
		/// <returns>Span of size <paramref name="exactSize"/></returns>
		/// <remarks>The cursor is advanced by <paramref name="exactSize"/></remarks>
		public Span<char> Allocate(int exactSize)
		{
			Contract.Positive(exactSize);

			// do we have enough space in the current segment?
			int newCount = this.Count + exactSize;
			if ((uint) newCount > (uint) this.Buffer.Length)
			{
				Grow(exactSize);
			}

			// we have enough remaining data to accomodate the requested size
			var count = this.Count;
			this.Count = newCount;
			return this.Buffer.AsSpan(count, exactSize);
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public void Advance(int count)
		{
			Contract.Positive(count);
			var newIndex = checked(this.Count + count);
			if ((uint) newIndex > (uint) this.Buffer.Length)
			{
				throw new ArgumentException("Cannot advance past the previously allocated buffer");
			}
			this.Count = newIndex;
			Contract.Debug.Ensures((uint) this.Count <= this.Buffer.Length);
		}

		/// <inheritdoc />
		public Memory<char> GetMemory(int sizeHint = 0)
		{
			Contract.Positive(sizeHint);

			// do we have enough space in the current segment?
			int remaining = this.Buffer.Length - this.Count;

			if (remaining <= 0 || (sizeHint != 0 && remaining < sizeHint))
			{
				Grow(sizeHint);
			}

			// we have enough remaining data to accomodate the requested size
			return this.Buffer.AsMemory(this.Count);
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public Span<char> GetSpan(int sizeHint = 0)
		{
			Contract.Positive(sizeHint);

			// do we have enough space in the current segment?
			int remaining = this.Buffer.Length - this.Count;

			if (remaining <= 0 || (sizeHint != 0 && remaining < sizeHint))
			{
				Grow(sizeHint);
			}

			// we have enough remaining data to accomodate the requested size
			return this.Buffer.AsSpan(this.Count);
		}

		#endregion

		#region Formatting...

		/// <summary>Writes the text representation of a 16-bit signed integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(sbyte value)
		{
#if NET9_0_OR_GREATER
			if (value >= 0)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityInt8);

			bool success = value.TryFormat(buf, out int written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 16-bit unsigned integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(byte value)
		{
#if NET9_0_OR_GREATER
			// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
			Write(value.ToString(default(IFormatProvider)));
#else

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityUInt8);

			bool success = value.TryFormat(buf, out int written); // will be inlined as Number.TryUInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
#endif
		}

		/// <summary>Writes the text representation of a 16-bit signed integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(short value)
		{
#if NET9_0_OR_GREATER
			if ((uint) value < 300U)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityInt16);

			bool success = value >= 0
				? value.TryFormat(buf, out var written) // will be inlined as Number.TryUInt32ToDecStr
				: value.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 16-bit unsigned integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(ushort value)
		{
#if NET9_0_OR_GREATER
			if (value < 300U)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityUInt16);

			bool success = value.TryFormat(buf, out int written); // will be inlined as Number.TryUInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 32-bit signed integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(int value)
		{
#if NET9_0_OR_GREATER
			if ((uint) value < 300U)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityInt32);

			bool success = value >= 0
				? value.TryFormat(buf, out var written) // will be inlined as Number.TryUInt32ToDecStr
				: value.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 32-bit unsigned integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(uint value)
		{
#if NET9_0_OR_GREATER
			if (value < 300U)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityUInt32);

			bool success = value.TryFormat(buf, out int written); // will be inlined as Number.TryUInt32ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 64-bit signed integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(long value)
		{
#if NET9_0_OR_GREATER
			if ((ulong) value < 300UL)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityInt64);

			bool success = value >= 0
				? value.TryFormat(buf, out var written) // will be inlined as Number.TryUInt64ToDecStr
				: value.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt64ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 64-bit unsigned integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(ulong value)
		{
#if NET9_0_OR_GREATER
			if (value < 300UL)
			{
				// small integers from 0 to 299 are cached by the runtime, so there will be no string allocation
				Write(value.ToString(default(IFormatProvider)));
				return;
			}
#endif

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityUInt64);

			bool success = value.TryFormat(buf, out int written); // will be inlined as Number.TryUInt64ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a <see cref="Guid"/>, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(Guid value)
		{
			Span<char> buf = GetSpan(StringConverters.Base16MaxCapacityGuid);

			bool success = value.TryFormat(buf, out int written);
			if (!success) StringConverters.ReportInternalFormattingError();
			
			Advance(written);
		}

		/// <summary>Writes the text representation of a <see cref="Uuid128"/>, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(Uuid128 value)
		{
			Span<char> buf = GetSpan(StringConverters.Base16MaxCapacityGuid);

			bool success = value.TryFormat(buf, out int written, default, null);
			if (!success) StringConverters.ReportInternalFormattingError();
			
			Advance(written);
		}

		/// <summary>Writes the text representation of a <see cref="Uuid64"/>, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(Uuid64 value)
		{
			Span<char> buf = GetSpan(StringConverters.Base16MaxCapacityUuid64);

			bool success = value.TryFormat(buf, out int written, default, null);
			if (!success) StringConverters.ReportInternalFormattingError();
			
			Advance(written);
		}

#if NET8_0_OR_GREATER

		/// <summary>Writes the text representation of a 64-bit signed integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(Int128 value)
		{
			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityInt128);

			bool success =
				  value >= 0 ? value.TryFormat(buf, out int written)
				: value.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo);
			if (!success) StringConverters.ReportInternalFormattingError();
			
			Advance(written);
		}

		/// <summary>Writes the text representation of a 64-bit unsigned integer, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(UInt128 value)
		{
			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityUInt128);

			bool success = value.TryFormat(buf, out int written); // will be inlined as Number.TryUInt128ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

#endif

		/// <summary>Writes the text representation of a 32-bit IEEE floating point number, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(float value)
		{

			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacitySingle);

			long x = unchecked((long) value);
			// ReSharper disable once CompareOfFloatsByEqualityOperator
			bool success =
				  x != value ? value.TryFormat(buf, out var written, "R", NumberFormatInfo.InvariantInfo)
				: x >= 0 ? x.TryFormat(buf, out written) // will be inlined as Number.TryUInt64ToDecStr
				: x.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt64ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 64-bit IEEE floating point number, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(double value)
		{
			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityDouble);

			long x = unchecked((long) value);
			// ReSharper disable once CompareOfFloatsByEqualityOperator
			bool success =
				  x != value ? value.TryFormat(buf, out var written, "R", NumberFormatInfo.InvariantInfo)
				: x >= 0 ? x.TryFormat(buf, out written) // will be inlined as Number.TryUInt64ToDecStr
				: x.TryFormat(buf, out written, default, NumberFormatInfo.InvariantInfo); // will be inlined as Number.TryNegativeInt64ToDecStr
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 128-bit decimal floating point number, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(decimal value)
		{
			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityDecimal);

			bool success = value.TryFormat(buf, out var written, default, NumberFormatInfo.InvariantInfo);
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		/// <summary>Writes the text representation of a 16-bit IEEE floating point number, using the Invariant culture</summary>
		/// <param name="value">Value to write</param>
		public void Write(Half value)
		{
			Span<char> buf = GetSpan(StringConverters.Base10MaxCapacityHalf);

			//note: I'm not sure how to optimize for this type...
			bool success = value.TryFormat(buf, out var written, null, NumberFormatInfo.InvariantInfo);
			if (!success) StringConverters.ReportInternalFormattingError();

			Advance(written);
		}

		#endregion

		/// <inheritdoc />
		public void Dispose()
		{
			Clear(release: true);
		}

	}

	[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
	internal class ValueStringWriterDebugView
	{
		public ValueStringWriterDebugView(ValueStringWriter buffer)
		{
			this.Text = buffer.ToString();
		}

		[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
		public string Text { get; set; }

	}

}
