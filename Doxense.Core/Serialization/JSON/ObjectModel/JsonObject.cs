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

#define CHECK_INVARIANTS

namespace Doxense.Serialization.Json
{
	using System.Buffers;
	using System.Collections.Frozen;
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using System.ComponentModel;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
	using System.Dynamic;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using System.Text;
	using Doxense.Memory;

	/// <summary>JSON Object with fields</summary>
	[Serializable]
	[DebuggerDisplay("JSON Object[{Count}]{GetMutabilityDebugLiteral(),nq} {GetCompactRepresentation(0),nq}")]
	[DebuggerTypeProxy(typeof(DebugView))]
	[DebuggerNonUserCode]
	[PublicAPI]
	public sealed class JsonObject : JsonValue, IDictionary<string, JsonValue>, IReadOnlyDictionary<string, JsonValue>, IEquatable<JsonObject>
	{
		// A JSON object can be writable (mutable), or read-only (immutable)
		// - Writable means that items can be added or removed from the "top-level" of this object.
		// - Read-only means that no items can be added or removed, BUT it does not mean that any children is itself readonly!
		// A JSON object can track the mutability of its children, and will maintain a flag it there was at least one mutable children at some point.
		// - We could track and update this state in real-time, but we are mostly interested in keeping track of readonly objects that were immutable from the moment of creation.

		/// <summary>Map of the properties of this object</summary>
		/// <remarks>If <see cref="m_readOnly"/> is not <see langword="0"/>, then any attempt to modify this dictionary should throw an exception</remarks>
		private readonly Dictionary<string, JsonValue> m_items;

		/// <summary>Defines the mutability of this object.</summary>
		/// <remarks>Mutability can change from Immutable to any of the ReadOnlyXYZ variants, but not the over way arround!</remarks>
		private bool m_readOnly;

		/// <summary>Returns a new empty JSON object</summary>
		[Obsolete("Use JsonObject.Create() for a mutable empty object, or JsonObject.EmptyReadOnly for an immutable empty singleton")]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public static JsonObject Empty => new(new(0, StringComparer.Ordinal), readOnly: false);

		/// <summary>Empty read-only JSON object singleton</summary>
		/// <remarks>This instance cannot be modified, and should be used to reduce memory allocations when working with read-only JSON</remarks>
		public static readonly JsonObject EmptyReadOnly = new(new(0, StringComparer.Ordinal), readOnly: true);

		#region Debug View...

		[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
		internal sealed class DebugView
		{
			private readonly JsonObject m_obj;

			public DebugView(JsonObject obj)
			{
				m_obj = obj;
			}

			[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
			public DebugViewItem[] Items
			{
				get
				{
					var tmp = m_obj.ToArray();
					var items = new DebugViewItem[tmp.Length];
					for (int i = 0; i < items.Length; ++i)
					{
						items[i] = new(tmp[i].Key, tmp[i].Value);
					}
					return items;
				}
			}

		}

		[DebuggerDisplay("{Value.GetCompactRepresentation(0),nq}", Name = "[{Key}]")]
		internal readonly struct DebugViewItem
		{
			public DebugViewItem(string key, JsonValue value)
			{
				this.Key = key;
				this.Value = value;
			}

			[DebuggerBrowsable(DebuggerBrowsableState.Never)]
			public string Key { [UsedImplicitly] get; }

			[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
			public JsonValue Value { [UsedImplicitly] get; }

		}

		#endregion

		#region Constructors...

		/// <summary>Creates a new JSON object that is empty</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public JsonObject()
			: this(0, StringComparer.Ordinal)
		{ }

		/// <summary>Creates a new JSON object that is empty and has the specified capacity</summary>
		/// <param name="capacity">The initial number of elements that the <see cref="JsonObject" /> can contain.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public JsonObject(int capacity)
			: this(capacity, StringComparer.Ordinal)
		{ }

		/// <summary>Creates a new JSON object that is empty, and uses the specified <see cref="T:System.Collections.Generic.IEqualityComparer`1" />.</summary>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="StringComparer.Ordinal">ordinal string comparer</see>.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public JsonObject(IEqualityComparer<string>? comparer) //REVIEW: remove this method and force to use ctor(capacity, comparer) instead?
		{
			m_items = new Dictionary<string, JsonValue>(0, comparer ?? StringComparer.Ordinal);
		}

		public JsonObject(int capacity, IEqualityComparer<string>? comparer)
		{
			Contract.Positive(capacity);
			m_items = new Dictionary<string, JsonValue>(capacity, comparer ?? StringComparer.Ordinal);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[Obsolete("Use obj.Copy() to clone an object")]
		public JsonObject(JsonObject copy)
		{
			Contract.NotNull(copy);
			
			// we have to create a defensive copy
			m_items = new Dictionary<string, JsonValue>(copy.m_items, copy.Comparer);
			CheckInvariants();
		}

		/// <summary>Creates a new <see cref="JsonObject"/> that will wrap the specified items</summary>
		/// <param name="items">Pre-computed map of elements</param>
		/// <param name="readOnly">If the object is marked as read-only</param>
		/// <remarks>If <paramref name="readOnly"/> is <see langword="true"/> then all elements in <paramref name="items"/> MUST also be read-only!</remarks>
		internal JsonObject(Dictionary<string, JsonValue> items, bool readOnly)
		{
			Contract.Debug.Requires(items != null);
			m_items = items;
			m_readOnly = readOnly;
			CheckInvariants();
		}

		[Conditional("CHECK_INVARIANTS")]
		private void CheckInvariants()
		{
#if CHECK_INVARIANTS
			if (m_readOnly)
			{
				foreach (var kv in m_items)
				{
					if (!kv.Value.IsReadOnly) Contract.Fail($"Immutable JSON object cannot contain mutable children '{kv.Key}' of type {kv.Value.Type}");
				}
			}
#endif
		}

		internal static JsonObject CreateEmptyWithComparer(IEqualityComparer<string>? comparer) => new(new (comparer ?? StringComparer.Ordinal), readOnly: false);

		internal static JsonObject CreateEmptyWithComparer<TValue>(IEqualityComparer<string>? comparer, [NoEnumeration] IEnumerable<KeyValuePair<string, TValue>>? items) => new(new (comparer ?? items switch
		{
			null => StringComparer.Ordinal,
			JsonObject obj => obj.m_items.Comparer,
			Dictionary<string, JsonValue> dic => dic.Comparer,
			FrozenDictionary<string, JsonValue> frz => frz.Comparer,
			ImmutableDictionary<string, JsonValue> imm => imm.KeyComparer,
			_ => StringComparer.Ordinal
		}), readOnly: false);

		/// <summary>Capture the KeyComparer used by an existing dictionary</summary>
		internal static IEqualityComparer<string>? ExtractKeyComparer<TValue>([NoEnumeration] IEnumerable<KeyValuePair<string, TValue>> items)
		{
			// if T == JsonOject or T == Dictionary<K,V>, we need to use the same key comparer as the original
			// ReSharper disable once SuspiciousTypeConversion.Global
			// ReSharper disable once ConstantNullCoalescingCondition
			return (items as JsonObject)?.Comparer ?? (items as Dictionary<string, TValue>)?.Comparer;
		}

		/// <summary>Freezes this object, once it has been initialized, by switching it to read-only mode.</summary>
		/// <remarks>Once "frozen", the operation cannot be reverted, and if additional mutation is required, a new copy of the object must be used.</remarks>
		public override JsonObject Freeze()
		{
			if (!m_readOnly)
			{ // at least one mutable children must be frozen as well!
				foreach (var value in m_items.Values)
				{
					value.Freeze();
				}
				m_readOnly = true;
			}

			CheckInvariants();
			return this;
		}

		internal JsonObject FreezeUnsafe()
		{
			if (m_items.Count == 0) return EmptyReadOnly;

			m_readOnly = true;
			CheckInvariants();
			return this;
		}

		/// <summary>Returns a new immutable read-only version of this JSON object (and all of its children)</summary>
		/// <returns>The same object, if it is already immutable; otherwise, a deep copy marked as read-only.</returns>
		/// <remarks>A JSON object that is immutable is truly safe against any modification, including any of its direct or indirect children.</remarks>
		public override JsonObject ToReadOnly()
		{
			if (m_readOnly)
			{
				CheckInvariants();
				return this;
			}

			var items = m_items;
			var map = new Dictionary<string, JsonValue>(items.Count, items.Comparer);
			foreach (var item in items)
			{
				var child = item.Value.ToReadOnly();
#if DEBUG
				Contract.Debug.Assert(child.IsReadOnly);
#endif
				map[item.Key] = child;
			}
			return new(map, readOnly: true);
		}


		/// <summary>Converts this JSON Object so that it, or any of its children that were previously read-only, can be mutated.</summary>
		/// <returns>The same instance if it is already fully mutable, OR a copy where any read-only Object or Array has been converted to allow mutations.</returns>
		/// <remarks>
		/// <para>Will return the same instance if it is already mutable, or a new deep copy with all children marked as mutable.</para>
		/// <para>This attempts to only copy what is necessary, and will not copy objects or arrays that are already mutable, or all other "value types" (strings, booleans, numbers, ...) that are always immutable.</para>
		/// </remarks>
		public override JsonObject ToMutable()
		{
			if (m_readOnly)
			{ // create a mutable copy
				return Copy();
			}

			// the top-level is mutable, but maybe it has read-only children?
			Dictionary<string, JsonValue>? copy = null;
			foreach (var (k, v) in m_items)
			{
				if (v is (JsonObject or JsonArray) && v.IsReadOnly)
				{
					copy ??= new (m_items.Count, m_items.Comparer);
					copy[k] = v.Copy();
				}
			}

			if (copy == null)
			{ // already mutable
				return this;
			}

			return new(copy, readOnly: false);
		}

		/// <summary>Returns a new mutable copy of this JSON array (and all of its children)</summary>
		/// <returns>A deep copy of this array and its children.</returns>
		/// <remarks>
		/// <para>This will recursively copy all JSON objects or arrays present in the array, even if they are already mutable.</para>
		/// <para>The new instance can be freely modified without any effect on its parent. Likewise, if the parent is modified, it will not have any effect on the copy.</para>
		/// </remarks>
		public override JsonObject Copy()
		{
			var items = m_items;
			if (items.Count == 0) return new JsonObject();

			var map = new Dictionary<string, JsonValue>(items.Count, items.Comparer);
			// we want to make sure that any mutable children is copied as well
			foreach (var kvp in items)
			{
				map[kvp.Key] = kvp.Value.Copy();
			}

			return new JsonObject(map, readOnly: false);
		}

		/// <summary>Creates a copy of this object</summary>
		/// <param name="deep">If <see langword="true" />, recursively copy the children as well. If <see langword="false" />, perform a shallow copy that reuse the same children.</param>
		/// <param name="readOnly">If <see langword="true" />, the copy will become read-only. If <see langword="false" />, the copy will be writable.</param>
		/// <returns>Copy of the object, and optionally of its children (if <paramref name="deep"/> is <see langword="true" /></returns>
		/// <remarks>Performing a deep copy will protect against any change, but will induce a lot of memory allocations. For example, any child array will be cloned even if they will not be modified later on.</remarks>
		protected internal override JsonObject Copy(bool deep, bool readOnly) => Copy(this, deep, readOnly);

		/// <summary>Creates a copy of a JSON object</summary>
		/// <param name="obj">Object to copy</param>
		/// <param name="deep">If <see langword="true" />, recursively copy the children as well. If <see langword="false" />, perform a shallow copy that reuse the same children.</param>
		/// <param name="readOnly">If <see langword="true" />, the copy will become read-only. If <see langword="false" />, the copy will be writable.</param>
		/// <returns>Copy of the object, and optionally of its children (if <paramref name="deep"/> is <see langword="true" /></returns>
		/// <remarks>Performing a deep copy will protect against any change, but will induce a lot of memory allocations. For example, any child array will be cloned even if they will not be modified later on.</remarks>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static JsonObject Copy(JsonObject obj, bool deep = false, bool readOnly = false)
		{
			Contract.NotNull(obj);

			if (readOnly)
			{
				return obj.ToReadOnly();
			}

			if (deep)
			{
				return obj.Copy();
			} 
			
			// simply create a shallow copy of the top-level
			var items = obj.m_items;
			return new JsonObject(new Dictionary<string, JsonValue>(items, items.Comparer), readOnly: false);
		}

		#region Create...

		#region Mutable...

		/// <summary>Create a new empty JSON object</summary>
		/// <returns>JSON object of size 0, that can be modified.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonObject Create() => new(new Dictionary<string, JsonValue>(0, StringComparer.Ordinal), readOnly: false);

		/// <summary>Create a new empty JSON object</summary>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>JSON object of size 0, that can be modified.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonObject Create(IEqualityComparer<string> comparer) => new(new Dictionary<string, JsonValue>(0, comparer), readOnly: false);

		/// <summary>Create a new JSON object with a single field</summary>
		/// <param name="key0">Name of the field</param>
		/// <param name="value0">Value of the field</param>
		/// <returns>JSON object of size 1, that can be modified.</returns>
		[Pure]
		public static JsonObject Create(string key0, JsonValue? value0) => new(new Dictionary<string, JsonValue>(1, StringComparer.Ordinal)
		{
			[key0] = value0 ?? JsonNull.Null
		}, readOnly: false);

		/// <summary>Create a new JSON object with a single field</summary>
		/// <param name="item">Name and value of the field</param>
		/// <returns>JSON object of size 1, that can be modified.</returns>
		[Pure]
		public static JsonObject Create((string Key, JsonValue? Value) item) => new(new Dictionary<string, JsonValue>(1, StringComparer.Ordinal)
		{
			[item.Key] = item.Value ?? JsonNull.Null
		}, readOnly: false);

		/// <summary>Create a new JSON object with 2 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <returns>JSON object of size 2, that can be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.Create([ (k, v), ... ]) instead.")]
		public static JsonObject Create(string key0, JsonValue? value0, string key1, JsonValue? value1) => new(new Dictionary<string, JsonValue>(2, StringComparer.Ordinal)
		{
			[key0] = value0 ?? JsonNull.Null,
			[key1] = value1 ?? JsonNull.Null,
		}, readOnly: false);

		/// <summary>Create a new JSON object with 2 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <returns>JSON object of size 2, that can be modified.</returns>
		[Pure]
		public static JsonObject Create(
			(string Key, JsonValue? Value) item1,
			(string Key, JsonValue? Value) item2
		) => new(new Dictionary<string, JsonValue>(2, StringComparer.Ordinal)
		{
			[item1.Key] = item1.Value ?? JsonNull.Null,
			[item2.Key] = item2.Value ?? JsonNull.Null,
		}, readOnly: false);

		/// <summary>Create a new JSON object with 3 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <param name="key2">Name of the third field</param>
		/// <param name="value2">Value of the third field</param>
		/// <returns>JSON object of size 3, that can be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.Create([ (k, v), ... ]) instead.")]
		public static JsonObject Create(string key0, JsonValue? value0, string key1, JsonValue? value1, string key2, JsonValue? value2) => new(new Dictionary<string, JsonValue>(3, StringComparer.Ordinal)
		{
			{ key0, value0 ?? JsonNull.Null },
			{ key1, value1 ?? JsonNull.Null },
			{ key2, value2 ?? JsonNull.Null },
		}, readOnly: false);

		/// <summary>Create a new JSON object with 3 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <param name="item3">Name and value of the third field</param>
		/// <returns>JSON object of size 3, that can be modified.</returns>
		[Pure]
		public static JsonObject Create(
			(string Key, JsonValue? Value) item1,
			(string Key, JsonValue? Value) item2,
			(string Key, JsonValue? Value) item3
		) => new(new Dictionary<string, JsonValue>(3, StringComparer.Ordinal)
		{
			[item1.Key] = item1.Value ?? JsonNull.Null,
			[item2.Key] = item2.Value ?? JsonNull.Null,
			[item3.Key] = item3.Value ?? JsonNull.Null,
		}, readOnly: false);

		/// <summary>Create a new JSON object with 4 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <param name="key2">Name of the third field</param>
		/// <param name="value2">Value of the third field</param>
		/// <param name="key3">Name of the fourth field</param>
		/// <param name="value3">Value of the fourth field</param>
		/// <returns>JSON object of size 4, that can be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.Create([ (k, v), ... ]) instead.")]
		public static JsonObject Create(string key0, JsonValue? value0, string key1, JsonValue? value1, string key2, JsonValue? value2, string key3, JsonValue? value3) => new(new Dictionary<string, JsonValue>(4, StringComparer.Ordinal)
		{
			{ key0, value0 ?? JsonNull.Null },
			{ key1, value1 ?? JsonNull.Null },
			{ key2, value2 ?? JsonNull.Null },
			{ key3, value3 ?? JsonNull.Null },
		}, readOnly: false);

		/// <summary>Create a new JSON object with 4 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <param name="item3">Name and value of the third field</param>
		/// <param name="item4">Name and value of the fourth field</param>
		/// <returns>JSON object of size 4, that can be modified.</returns>
		[Pure]
		public static JsonObject Create(
			(string Key, JsonValue? Value) item1,
			(string Key, JsonValue? Value) item2,
			(string Key, JsonValue? Value) item3,
			(string Key, JsonValue? Value) item4
		) => new(new Dictionary<string, JsonValue>(4, StringComparer.Ordinal)
		{
			[item1.Key] = item1.Value ?? JsonNull.Null,
			[item2.Key] = item2.Value ?? JsonNull.Null,
			[item3.Key] = item3.Value ?? JsonNull.Null,
			[item4.Key] = item4.Value ?? JsonNull.Null,
		}, readOnly: false);

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(IDictionary<string, JsonValue> items)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(null, items).AddRange(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(ReadOnlySpan<KeyValuePair<string, JsonValue>> items, IEqualityComparer<string>? comparer = null)
		{
			return CreateEmptyWithComparer(comparer).AddRange(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(ReadOnlySpan<(string Key, JsonValue? Value)> items)
		{
			//note: this overload without optional IEqualityComparer is required to resolve an overload amgibuity with the Create(ReadOnlySpan<KeyValuePair<string, JsonValue>>) variant when calling JsonObject.Create([])
			// => it seems that if one of the two has an optional argument, it will have a lower priority.

			return Create().AddRange(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject FromValues<TValue>(ReadOnlySpan<(string Key, TValue Value)> items)
		{
			//note: this overload without optional IEqualityComparer is required to resolve an overload amgibuity with the Create(ReadOnlySpan<KeyValuePair<string, JsonValue>>) variant when calling JsonObject.Create([])
			// => it seems that if one of the two has an optional argument, it will have a lower priority.

			return Create().AddValues(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(ReadOnlySpan<(string Key, JsonValue? Value)> items, IEqualityComparer<string>? comparer)
		{
			return CreateEmptyWithComparer(comparer).AddRange(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(KeyValuePair<string, JsonValue>[] items, IEqualityComparer<string>? comparer)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRange(items.AsSpan());
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create((string Key, JsonValue? Value)[] items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRange(items.AsSpan());
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(IEnumerable<KeyValuePair<string, JsonValue>> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer, items).AddRange(items);
		}

		/// <summary>Create a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer"></param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject Create(IEnumerable<(string Key, JsonValue Value)> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRange(items);
		}

		#endregion

		#region Immutable...

		/// <summary>Creates a new empty read-only JSON object</summary>
		/// <returns>JSON object of size 0, that cannot be modified.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonObject CreateReadOnly() => EmptyReadOnly;

		/// <summary>Creates a new empty read-only JSON object</summary>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>JSON object of size 0, that cannot be modified.</returns>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static JsonObject CreateReadOnly(IEqualityComparer<string>? comparer)
		{
			comparer ??= StringComparer.Ordinal;
			return ReferenceEquals(comparer, StringComparer.Ordinal) ? EmptyReadOnly : new(new(0, comparer), readOnly: true);
		}

		/// <summary>Creates a new immutable JSON object with a single field</summary>
		/// <param name="key0">Name of the field</param>
		/// <param name="value0">Value of the field</param>
		/// <returns>JSON object of size 1, that cannot be modified.</returns>
		[Pure]
		public static JsonObject CreateReadOnly(string key0, JsonValue? value0) => new(new Dictionary<string, JsonValue>(1, StringComparer.Ordinal)
		{
			[key0] = (value0 ?? JsonNull.Null).ToReadOnly()
		}, readOnly: true);

		/// <summary>Creates a new immutable JSON object with a single field</summary>
		/// <param name="item">Name and value of the field</param>
		/// <returns>JSON object of size 1, that cannot be modified.</returns>
		[Pure]
		public static JsonObject CreateReadOnly((string Key, JsonValue? Value) item) => new(new Dictionary<string, JsonValue>(1, StringComparer.Ordinal)
		{
			[item.Key] = (item.Value ?? JsonNull.Null).ToReadOnly()
		}, readOnly: true);

		/// <summary>Creates a new immutable JSON object with 2 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <returns>JSON object of size 2, that cannot be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.CreateReadOnly([ (k, v), ... ]) instead.")]
		public static JsonObject CreateReadOnly(string key0, JsonValue? value0, string key1, JsonValue? value1) => new(new Dictionary<string, JsonValue>(2, StringComparer.Ordinal)
		{
			{ key0, (value0 ?? JsonNull.Null).ToReadOnly() },
			{ key1, (value1 ?? JsonNull.Null).ToReadOnly() },
		}, readOnly: true);

		/// <summary>Creates a new immutable JSON object with 2 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <returns>JSON object of size 2, that cannot be modified.</returns>
		[Pure]
		public static JsonObject CreateReadOnly((string Key, JsonValue? Value) item1, (string Key, JsonValue? Value) item2) => new(new Dictionary<string, JsonValue>(2, StringComparer.Ordinal)
		{
			[item1.Key] = (item1.Value ?? JsonNull.Null).ToReadOnly(),
			[item2.Key] = (item2.Value ?? JsonNull.Null).ToReadOnly(),
		}, readOnly: true);

		/// <summary>Creates a new immutable JSON object with 3 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <param name="key2">Name of the third field</param>
		/// <param name="value2">Value of the third field</param>
		/// <returns>JSON object of size 3, that cannot be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.CreateReadOnly([ (k, v), ... ]) instead.")]
		public static JsonObject CreateReadOnly(string key0, JsonValue? value0, string key1, JsonValue? value1, string key2, JsonValue? value2) => new(new Dictionary<string, JsonValue>(3, StringComparer.Ordinal)
		{
			{ key0, (value0 ?? JsonNull.Null).ToReadOnly() },
			{ key1, (value1 ?? JsonNull.Null).ToReadOnly() },
			{ key2, (value2 ?? JsonNull.Null).ToReadOnly() },
		}, readOnly: true);

		/// <summary>Creates a new immutable JSON object with 3 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <param name="item3">Name and value of the third field</param>
		/// <returns>JSON object of size 2, that cannot be modified.</returns>
		[Pure]
		public static JsonObject CreateReadOnly(
			(string Key, JsonValue? Value) item1,
			(string Key, JsonValue? Value) item2,
			(string Key, JsonValue? Value) item3
		) => new(new Dictionary<string, JsonValue>(3, StringComparer.Ordinal)
		{
			[item1.Key] = (item1.Value ?? JsonNull.Null).ToReadOnly(),
			[item2.Key] = (item2.Value ?? JsonNull.Null).ToReadOnly(),
			[item3.Key] = (item3.Value ?? JsonNull.Null).ToReadOnly(),
		}, readOnly: true);

		/// <summary>Creates an immutable new JSON object with 4 fields</summary>
		/// <param name="key0">Name of the first field</param>
		/// <param name="value0">Value of the first field</param>
		/// <param name="key1">Name of the second field</param>
		/// <param name="value1">Value of the second field</param>
		/// <param name="key2">Name of the third field</param>
		/// <param name="value2">Value of the third field</param>
		/// <param name="key3">Name of the fourth field</param>
		/// <param name="value3">Value of the fourth field</param>
		/// <returns>JSON object of size 4, that cannot be modified.</returns>
		[Pure]
		[Obsolete("Please use the JsonObject.CreateReadOnly([ (k, v), ... ]) instead.")]
		public static JsonObject CreateReadOnly(string key0, JsonValue? value0, string key1, JsonValue? value1, string key2, JsonValue? value2, string key3, JsonValue? value3) => new(new Dictionary<string, JsonValue>(4, StringComparer.Ordinal)
		{
			{ key0, (value0 ?? JsonNull.Null).ToReadOnly() },
			{ key1, (value1 ?? JsonNull.Null).ToReadOnly() },
			{ key2, (value2 ?? JsonNull.Null).ToReadOnly() },
			{ key3, (value3 ?? JsonNull.Null).ToReadOnly() },
		}, readOnly: false);

		/// <summary>Creates a new immutable JSON object with 4 fields</summary>
		/// <param name="item1">Name and value of the first field</param>
		/// <param name="item2">Name and value of the second field</param>
		/// <param name="item3">Name and value of the third field</param>
		/// <param name="item4">Name and value of the fourth field</param>
		/// <returns>JSON object of size 2, that cannot be modified.</returns>
		[Pure]
		public static JsonObject CreateReadOnly(
			(string Key, JsonValue? Value) item1,
			(string Key, JsonValue? Value) item2,
			(string Key, JsonValue? Value) item3,
			(string Key, JsonValue? Value) item4
		) => new(new Dictionary<string, JsonValue>(4, StringComparer.Ordinal)
		{
			[item1.Key] = (item1.Value ?? JsonNull.Null).ToReadOnly(),
			[item2.Key] = (item2.Value ?? JsonNull.Null).ToReadOnly(),
			[item3.Key] = (item3.Value ?? JsonNull.Null).ToReadOnly(),
			[item4.Key] = (item4.Value ?? JsonNull.Null).ToReadOnly(),
		}, readOnly: true);

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(IDictionary<string, JsonValue> items)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(null, items).AddRangeReadOnly(items).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(ReadOnlySpan<KeyValuePair<string, JsonValue>> items, IEqualityComparer<string>? comparer = null)
		{
			return CreateEmptyWithComparer(comparer).AddRangeReadOnly(items).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(ReadOnlySpan<(string Key, JsonValue? Value)> items, IEqualityComparer<string>? comparer = null)
		{
			return CreateEmptyWithComparer(comparer).AddRangeReadOnly(items).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(KeyValuePair<string, JsonValue>[] items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRangeReadOnly(items.AsSpan()).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly((string Key, JsonValue?)[] items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRangeReadOnly(items.AsSpan()).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(IEnumerable<KeyValuePair<string, JsonValue>> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer, items).AddRangeReadOnly(items).FreezeUnsafe();
		}

		/// <summary>Creates a new JSON object with the specified items</summary>
		/// <param name="items">Map of key/values to copy</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>New JSON object with the same elements in <see cref="items"/></returns>
		/// <remarks>Adding or removing items in this new object will not modify <paramref name="items"/> (and vice versa), but any change to a mutable children will be reflected in both.</remarks>
		public static JsonObject CreateReadOnly(IEnumerable<(string Key, JsonValue Value)> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer).AddRangeReadOnly(items).FreezeUnsafe();
		}

		#endregion

		#endregion

		#region FromValues...

		/// <summary>Creates a JSON Object from a sequence of key/value pairs.</summary>
		/// <typeparam name="TValue">Type of the values, that must support conversion to JSON values</typeparam>
		/// <param name="items">Sequence of key/value pairs that will become the fields of the new JSON Object. There must not be any duplicate key, or an exception will be thrown.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that can be modified.</returns>
		public static JsonObject FromValues<TValue>(IEnumerable<KeyValuePair<string, TValue>> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer, items).AddValues(items);
		}

		/// <summary>Creates a read-only JSON Object from a list of key/value pairs.</summary>
		/// <typeparam name="TValue"></typeparam>
		/// <param name="items">Sequence of key/value pairs that will become the fields of the new JSON Object. There must not be any duplicate key, or an exception will be thrown.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that cannot be modified.</returns>
		public static JsonObject FromValuesReadOnly<TValue>(IEnumerable<KeyValuePair<string, TValue>> items, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(items);
			return CreateEmptyWithComparer(comparer, items).AddValuesReadOnly(items).FreezeUnsafe();
		}

		/// <summary>Creates a JSON Object from an existing dictionary, using a custom JSON converter.</summary>
		/// <typeparam name="TValue">Type of the values, that must support conversion to JSON values</typeparam>
		/// <param name="members">Dictionary that must be converted.</param>
		/// <param name="valueSelector">Handler that is called for each value of the dictionary, and must return the converted JSON value.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that can be modified.</returns>
		public static JsonObject FromValues<TValue>(IDictionary<string, TValue> members, Func<TValue, JsonValue?> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(members);

			comparer ??= ExtractKeyComparer(members) ?? StringComparer.Ordinal;

			var items = new Dictionary<string, JsonValue>(members.Count, comparer);
			foreach (var kvp in members)
			{
				items.Add(kvp.Key, valueSelector(kvp.Value) ?? JsonNull.Missing);
			}
			return new JsonObject(items, readOnly: false);
		}

		/// <summary>Creates a read-only JSON Object from an existing dictionary, using a custom JSON converter.</summary>
		/// <typeparam name="TValue">Type of the values, that must support conversion to JSON values</typeparam>
		/// <param name="members">Dictionary that must be converted.</param>
		/// <param name="valueSelector">Handler that is called for each value of the dictionary, and must return the converted JSON value.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that cannot be modified.</returns>
		public static JsonObject FromValuesReadOnly<TValue>(IDictionary<string, TValue> members, Func<TValue, JsonValue?> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(members);

			comparer ??= ExtractKeyComparer(members) ?? StringComparer.Ordinal;

			var items = new Dictionary<string, JsonValue>(members.Count, comparer);
			foreach (var kvp in members)
			{
				items.Add(kvp.Key, (valueSelector(kvp.Value) ?? JsonNull.Missing).ToReadOnly());
			}
			return new JsonObject(items, readOnly: true);
		}

		/// <summary>Creates a JSON Object from a sequence of elements, using a custom key and value selector.</summary>
		/// <typeparam name="TElement">Types of elements to be converted into key/value pairs.</typeparam>
		/// <param name="source">Sequence of elements to convert</param>
		/// <param name="keySelector">Handler that is called for each element of the sequence, and should return the corresponding unique key.</param>
		/// <param name="valueSelector">Handler that is called for each element of the sequence, and should return the corresponding JSON value.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that can be modified</returns>
		public static JsonObject FromValues<TElement>(IEnumerable<TElement> source, Func<TElement, string> keySelector, Func<TElement, JsonValue?> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			var map = new Dictionary<string, JsonValue>(source.TryGetNonEnumeratedCount(out var count) ? count : 0, comparer ?? StringComparer.Ordinal);
			foreach (var item in source)
			{
				map.Add(keySelector(item), valueSelector(item) ?? JsonNull.Missing);
			}
			return new JsonObject(map, readOnly: false);
		}

		/// <summary>Creates a read-only JSON Object from a sequence of elements, using a custom key and value selector.</summary>
		/// <typeparam name="TElement">Types of elements to be converted into key/value pairs.</typeparam>
		/// <param name="source">Sequence of elements to convert</param>
		/// <param name="keySelector">Handler that is called for each element of the sequence, and should return the corresponding unique key.</param>
		/// <param name="valueSelector">Handler that is called for each element of the sequence, and should return the corresponding JSON value.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that cannot be modified</returns>
		public static JsonObject FromValuesReadOnly<TElement>(IEnumerable<TElement> source, Func<TElement, string> keySelector, Func<TElement, JsonValue?> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			var map = new Dictionary<string, JsonValue>(source.TryGetNonEnumeratedCount(out var count) ? count : 0, comparer ?? StringComparer.Ordinal);
			foreach (var item in source)
			{
				map.Add(keySelector(item), (valueSelector(item) ?? JsonNull.Missing).ToReadOnly());
			}
			return new JsonObject(map, readOnly: true);
		}

		/// <summary>Creates a JSON Object from a sequence of elements, using a custom key and value selector.</summary>
		/// <typeparam name="TElement">Types of elements to be converted into key/value pairs.</typeparam>
		/// <typeparam name="TValue">Type of the extracted values, that must support conversion to JSON values</typeparam>
		/// <param name="source">Sequence of elements to convert</param>
		/// <param name="keySelector">Handler that is called for each element of the sequence, and should return the corresponding unique key.</param>
		/// <param name="valueSelector">Handler that is called for each element of the sequence, and should return the corresponding value, that will in turn be converted into JSON.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that can be modified</returns>
		public static JsonObject FromValues<TElement, TValue>(IEnumerable<TElement> source, Func<TElement, string> keySelector, Func<TElement, TValue> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			var map = new Dictionary<string, JsonValue>(source.TryGetNonEnumeratedCount(out var count) ? count : 0, comparer ?? StringComparer.Ordinal);
			var context = new CrystalJsonDomWriter.VisitingContext();
			foreach (var item in source)
			{
				var child = FromValue(CrystalJsonDomWriter.Default, ref context, valueSelector(item));
				map.Add(keySelector(item), child);
			}
			return new JsonObject(map, readOnly: false);
		}

		/// <summary>Creates a read-only JSON Object from a sequence of elements, using a custom key and value selector.</summary>
		/// <typeparam name="TElement">Types of elements to be converted into key/value pairs.</typeparam>
		/// <typeparam name="TValue">Type of the extracted values, that must support conversion to JSON values</typeparam>
		/// <param name="source">Sequence of elements to convert</param>
		/// <param name="keySelector">Handler that is called for each element of the sequence, and should return the corresponding unique key.</param>
		/// <param name="valueSelector">Handler that is called for each element of the sequence, and should return the corresponding value, that will in turn be converted into JSON.</param>
		/// <param name="comparer">The <see cref="T:System.Collections.Generic.IEqualityComparer`1" /> implementation to use when comparing keys, or <see langword="null" /> to use the default <see cref="T:System.Collections.Generic.EqualityComparer`1" /> for the type of the key.</param>
		/// <returns>Corresponding JSON object, that cannot be modified</returns>
		public static JsonObject FromValuesReadOnly<TElement, TValue>(IEnumerable<TElement> source, Func<TElement, string> keySelector, Func<TElement, TValue> valueSelector, IEqualityComparer<string>? comparer = null)
		{
			var map = new Dictionary<string, JsonValue>(source.TryGetNonEnumeratedCount(out var count) ? count : 0, comparer ?? StringComparer.Ordinal);
			var context = new CrystalJsonDomWriter.VisitingContext();
			foreach (var item in source)
			{
				var child = FromValue(CrystalJsonDomWriter.DefaultReadOnly, ref context, valueSelector(item));
				Contract.Debug.Assert(child.IsReadOnly);
				map.Add(keySelector(item), child);
			}
			return new JsonObject(map, readOnly: true);
		}

		#endregion

		#region FromObject...

		/// <summary>Converts an instance of type <typeparamref name="TValue"/> into the equivalent JSON Object.</summary>
		/// <typeparam name="TValue">Publicly known type of the instance.</typeparam>
		/// <param name="value">Instance to convert.</param>
		/// <returns>Corresponding JSON Object, or <see langword="null"/> if <paramref name="value"/> is null</returns>
		/// <remarks>The JSON Object that is returned is mutable, and cannot safely be cached or shared. If you need an immutable instance, consider calling <see cref="FromObjectReadOnly{TValue}(TValue)"/> instead.</remarks>
		[return: NotNullIfNotNull(nameof(value))]
		public static JsonObject? FromObject<TValue>(TValue value)
		{
			//REVIEW: que faire si c'est null? Json.Net throw une ArgumentNullException dans ce cas, et ServiceStack ne gère pas de DOM de toutes manières...
			return CrystalJsonDomWriter.Default.ParseObject(value, typeof(TValue)).AsObjectOrDefault();
		}

		/// <summary>Converts an instance of type <typeparamref name="TValue"/> into the equivalent read-only JSON Object.</summary>
		/// <typeparam name="TValue">Publicly known type of the instance.</typeparam>
		/// <param name="value">Instance to convert.</param>
		/// <returns>Corresponding immutable JSON Object, or <see langword="null"/> if <paramref name="value"/> is null</returns>
		/// <remarks>The JSON Object that is returned is read-only, and can safely be cached or shared. If you need a mutable instance, consider calling <see cref="FromObject{TValue}(TValue)"/> instead.</remarks>
		[return: NotNullIfNotNull(nameof(value))]
		public static JsonObject? FromObjectReadOnly<TValue>(TValue value)
		{
			//REVIEW: que faire si c'est null? Json.Net throw une ArgumentNullException dans ce cas, et ServiceStack ne gère pas de DOM de toutes manières...
			return CrystalJsonDomWriter.DefaultReadOnly.ParseObject(value, typeof(TValue)).AsObjectOrDefault();
		}

		/// <summary>Converts an instance of type <typeparamref name="TValue"/> into the equivalent JSON Object.</summary>
		/// <typeparam name="TValue">Publicly known type of the instance.</typeparam>
		/// <param name="value">Instance to convert.</param>
		/// <param name="settings">Serialization settings (use default JSON settings if null)</param>
		/// <param name="resolver">Custom type resolver (use default behavior if null)</param>
		/// <returns>Corresponding JSON Object, or <see langword="null"/> if <paramref name="value"/> is null</returns>
		/// <remarks>The JSON Object that is returned is mutable, and cannot safely be cached or shared. If you need an immutable instance, consider calling <see cref="FromObjectReadOnly{TValue}(TValue)"/> instead.</remarks>
		[return: NotNullIfNotNull(nameof(value))]
		public static JsonObject? FromObject<TValue>(TValue value, CrystalJsonSettings settings, ICrystalJsonTypeResolver? resolver = null)
		{
			return CrystalJsonDomWriter.Create(settings, resolver).ParseObject(value, typeof(TValue)).AsObjectOrDefault();
		}

		/// <summary>Converts an instance of type <typeparamref name="TValue"/> into the equivalent read-only JSON Object.</summary>
		/// <typeparam name="TValue">Publicly known type of the instance.</typeparam>
		/// <param name="value">Instance to convert.</param>
		/// <param name="settings">Serialization settings (use default JSON settings if null)</param>
		/// <param name="resolver">Custom type resolver (use default behavior if null)</param>
		/// <returns>Corresponding immutable JSON Object, or <see langword="null"/> if <paramref name="value"/> is null</returns>
		/// <remarks>The JSON Object that is returned is read-only, and can safely be cached or shared. If you need a mutable instance, consider calling <see cref="FromObject{TValue}(TValue)"/> instead.</remarks>
		[return: NotNullIfNotNull(nameof(value))]
		public static JsonObject? FromObjectReadOnly<TValue>(TValue value, CrystalJsonSettings settings, ICrystalJsonTypeResolver? resolver = null)
		{
			return CrystalJsonDomWriter.CreateReadOnly(settings, resolver).ParseObject(value, typeof(TValue)).AsObjectOrDefault();
		}

		#endregion

		/// <summary>Converts an untyped dictionary into a JSON Object</summary>
		/// <returns>Corresponding mutable JSON Object</returns>
		/// <remarks>This should only be used to interface with legacy APIs that generate a <see cref="Dictionary{TKey,TValue}">Dictionary&lt;string, object></see>.</remarks>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static JsonObject CreateBoxed(IDictionary<string, object> members, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(members);

			var map = new Dictionary<string, JsonValue>(members.Count, comparer ?? ExtractKeyComparer(members) ?? StringComparer.Ordinal);
			foreach (var kvp in members)
			{
				map.Add(kvp.Key, FromValue(kvp.Value));
			}
			return new JsonObject(map, readOnly: false);
		}

		/// <summary>Converts an untyped dictionary into a JSON Object</summary>
		/// <returns>Corresponding immutable JSON Object</returns>
		/// <remarks>This should only be used to interface with legacy APIs that generate a <see cref="Dictionary{TKey,TValue}">Dictionary&lt;string, object></see>.</remarks>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static JsonObject CreateBoxedReadOnly(IDictionary<string, object> members, IEqualityComparer<string>? comparer = null)
		{
			Contract.NotNull(members);

			var map = new Dictionary<string, JsonValue>(members.Count, comparer ?? ExtractKeyComparer(members) ?? StringComparer.Ordinal);
			foreach (var kvp in members)
			{
				map.Add(kvp.Key, FromValueReadOnly(kvp.Value));
			}
			return new JsonObject(map, readOnly: true);
		}
		
#if NET8_0_OR_GREATER
		[Obsolete]
#endif
		private static System.Runtime.Serialization.FormatterConverter? CachedFormatterConverter;

		/// <summary>Serializes an <see cref="Exception"/> into a JSON object</summary>
		/// <returns></returns>
		/// <remarks>
		/// The exception must implement <see cref="System.Runtime.Serialization.ISerializable"/>, and CANNOT contain cycles or self-references!
		/// The JSON object produced MAY NOT be deserializable back into the original exception type!
		/// </remarks>
		[Pure]
#if NET8_0_OR_GREATER
		[Obsolete("Formatter-based serialization is obsolete and should not be used.")]
		[EditorBrowsable(EditorBrowsableState.Never)]
#endif
		public static JsonObject FromException(Exception ex, bool includeTypes = true)
		{
			Contract.NotNull(ex);
			if (ex is not System.Runtime.Serialization.ISerializable ser)
			{
				throw new JsonSerializationException($"Cannot serialize exception of type '{ex.GetType().FullName}' because it is not marked as Serializable.");
			}

			return FromISerializable(ser, includeTypes);
		}

		/// <summary>Serializes a type that implements <see cref="System.Runtime.Serialization.ISerializable"/> into a JSON object representation</summary>
		/// <remarks>
		/// The JSON object produced MAY NOT be deserializable back into the original exception type!
		/// </remarks>
		[Pure]
#if NET8_0_OR_GREATER
		[Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
		[EditorBrowsable(EditorBrowsableState.Never)]
#endif
		public static JsonObject FromISerializable(System.Runtime.Serialization.ISerializable value, bool includeTypes = true, CrystalJsonSettings? settings = null, ICrystalJsonTypeResolver? resolver = null)
		{
			Contract.NotNull(value);

			settings ??= CrystalJsonSettings.Json;
			resolver ??= CrystalJson.DefaultResolver;

			var formatter = CachedFormatterConverter ??= new System.Runtime.Serialization.FormatterConverter();
			var info = new System.Runtime.Serialization.SerializationInfo(value.GetType(), formatter);
			var ctx = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.Persistence);

			value.GetObjectData(info, ctx);

			var obj = new JsonObject();
			var it = info.GetEnumerator();
			{
				while (it.MoveNext())
				{
					object? x = it.Value;
					if (includeTypes)
					{ // round-trip mode: "NAME: [ TYPE, VALUE ]"
						var v = x is System.Runtime.Serialization.ISerializable ser
							? FromISerializable(ser, includeTypes: true, settings: settings, resolver: resolver)
							: FromValue(x, it.ObjectType, settings, resolver);
						// even if the value is null, we still have to provide the type!
						obj[it.Name] = JsonArray.Create(JsonString.Return(it.ObjectType), v);
					}
					else
					{ // compact mode: "NAME: VALUE"

						// since we don't care to be deserializable, we can ommit 'null' items
						if (x == null) continue;

						var v = x is System.Runtime.Serialization.ISerializable ser
							? FromISerializable(ser, includeTypes: false, settings: settings, resolver: resolver)
							: FromValue(x, settings, resolver);

						obj[it.Name] = v;
					}
				}
			}

			return obj;
		}

		#endregion

		public int Count => m_items.Count;

		ICollection<string> IDictionary<string, JsonValue>.Keys => m_items.Keys;

		IEnumerable<string> IReadOnlyDictionary<string, JsonValue>.Keys => m_items.Keys;

		public Dictionary<string, JsonValue>.KeyCollection Keys => m_items.Keys;

		ICollection<JsonValue> IDictionary<string, JsonValue>.Values => m_items.Values;

		IEnumerable<JsonValue> IReadOnlyDictionary<string, JsonValue>.Values => m_items.Values;

		public Dictionary<string, JsonValue>.ValueCollection Values => m_items.Values;

		public Dictionary<string, JsonValue>.Enumerator GetEnumerator() => m_items.GetEnumerator();

		IEnumerator<KeyValuePair<string, JsonValue>> IEnumerable<KeyValuePair<string, JsonValue>>.GetEnumerator() => m_items.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => m_items.GetEnumerator();

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public override bool IsReadOnly => m_readOnly;

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public IEqualityComparer<string> Comparer => m_items.Comparer;

		[EditorBrowsable(EditorBrowsableState.Always)]
		[AllowNull]
		public override JsonValue this[string key]
		{
			[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => m_items.TryGetValue(key, out var value) ? value : JsonNull.Missing;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set => Set(key, value);
		}

		[EditorBrowsable(EditorBrowsableState.Always)]
		[AllowNull]
		public override JsonValue this[ReadOnlySpan<char> key]
		{
			[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => TryGetValue(key, out var value) ? value : JsonNull.Missing;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set => Set(key, value);
		}

		[EditorBrowsable(EditorBrowsableState.Always)]
		[ContractAnnotation("halt<=key:null; =>true,value:notnull; =>false,value:null")]
		public override bool TryGetValue(string key, [MaybeNullWhen(false)] out JsonValue value)
		{
			return m_items.TryGetValue(key, out value);
		}

		/// <inheritdoc/>
		[EditorBrowsable(EditorBrowsableState.Always)]
		[ContractAnnotation("halt<=key:null; =>true,value:notnull; =>false,value:null")]
		public override bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out JsonValue value)
		{
#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out value);
#else
			// We cannot use span lookups so we may need to allocate the string in order to find it :(
			// - if the object is _small_ (1 or 2 keys?) AND uses the ordinal comparer, then simply enumerating the key/value pairs and calling SequenceEqual would be quicker (in theoriy no allocation)
			// - for larger objects we eat the cost and allocate, hoping to be able to optimize this in the feature with span lookups

			var items = m_items;
			switch (items.Count)
			{
				case 0:
				{
					value = null;
					return false;
				}
				case <= 3 when ReferenceEquals(items.Comparer, StringComparer.Ordinal):
				{
					foreach (var kv in items)
					{
						if (key.SequenceEqual(kv.Key.AsSpan()))
						{
							value = kv.Value;
							return true;
						}
					}
					value = null;
					return false;
				}
				default:
				{
					//PERF: we unfortunately need to allocate the string :(
					return items.TryGetValue(key.ToString(), out value);
				}
			}
#endif
		}

#if NET9_0_OR_GREATER

		/// <inheritdoc/>
		[EditorBrowsable(EditorBrowsableState.Always)]
		[ContractAnnotation("halt<=key:null; =>true,value:notnull; =>false,value:null")]
		public override bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out string actualKey, [MaybeNullWhen(false)] out JsonValue value)
		{
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out actualKey, out value);
		}

#endif

		/// <inheritdoc/>
		[EditorBrowsable(EditorBrowsableState.Always)]
		[ContractAnnotation("halt<=key:null; =>true,value:notnull; =>false,value:null")]
		public override bool TryGetValue(ReadOnlyMemory<char> key, [MaybeNullWhen(false)] out JsonValue value)
		{
#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key.Span, out value);
#else
			if (key.TryGetString(out var k))
			{ // we have the whole string, we can do the standard lookup
				return m_items.TryGetValue(k, out value);
			}
			return TryGetValue(key.Span, out value);
#endif
		}

		/// <inheritdoc/>
		[EditorBrowsable(EditorBrowsableState.Always)]
		public void Add(string key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.Debug.Requires(key != null && !ReferenceEquals(this, value));
			m_items.Add(key, value ?? JsonNull.Null);
		}

		public void Add(ReadOnlySpan<char> key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.Debug.Requires(!ReferenceEquals(this, value));
			m_items.Add(key.ToString(), value ?? JsonNull.Null);
		}

		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool TryAdd(string key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			return m_items.TryAdd(key, value ?? JsonNull.Null);
		}

		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool TryAdd(ReadOnlySpan<char> key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().TryAdd(key, value ?? JsonNull.Null);
#else
			return m_items.TryAdd(key.ToString(), value ?? JsonNull.Null);
#endif
		}

		/// <inheritdoc/>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public void Add(KeyValuePair<string, JsonValue> item)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
			m_items.Add(item.Key, item.Value ?? JsonNull.Null);
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public void Add((string Key, JsonValue? Value) item)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
			m_items.Add(item.Key, item.Value ?? JsonNull.Null);
		}

		#region CopyAndXYZ()...

		private static void MakeReadOnly(Dictionary<string, JsonValue> items)
		{
			foreach (var kv in items)
			{
				if (!kv.Value.IsReadOnly)
				{
					items[kv.Key] = kv.Value.ToReadOnly();
				}
			}
		}

		/// <summary>Returns a new read-only copy of this object with an additional item</summary>
		/// <param name="key">Name of the field to add. If a field with the same name already exists, an exception will be thrown.</param>
		/// <param name="value">Value of the new item</param>
		/// <returns>A new instance with the same content of the original object, plus the additional item</returns>
		/// <remarks>
		/// <para>If a field with the same name already exists, an exception will be thrown.</para>
		/// <para>If the object was not-readonly, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already-readonly objects, and with read-only values.</para>
		/// </remarks>
		[Pure, MustUseReturnValue]
		public JsonObject CopyAndAdd(string key, JsonValue? value)
		{
			// copy and add the new value
			var items = new Dictionary<string, JsonValue>(m_items);
			items.Add(key, value?.ToReadOnly() ?? JsonNull.Null);

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			return new(items, readOnly: true);
		}

		/// <summary>Replaces a published JSON Object with a new version with an added field, in a thread-safe manner, using a <see cref="SpinWait"/> if necessary.</summary>
		/// <param name="original">Reference to the currently published JSON Object</param>
		/// <param name="key">Name of the field to add. If a field with the same name already exists, an exception will be thrown.</param>
		/// <param name="value">Value of the field to add</param>
		/// <returns>New published JSON Object, that includes the new field.</returns>
		/// <remarks>
		/// <para>This method will attempt to atomically replace the original JSON Object with a new version, unless another thread was able to update it faster, in which case it will simply retry with the newest version, until it is able to successfully update the reference.</para>
		/// <para>Caution: the order of operation between threads is not guaranteed, and this method _may_ loop infinitely if it is perpetually blocked by another, faster, thread !</para>
		/// </remarks>
		public static JsonObject CopyAndAdd(ref JsonObject original, string key, JsonValue? value)
		{
			var snapshot = Volatile.Read(ref original);
			var copy = snapshot.CopyAndAdd(key, value);

			return ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot))
				? copy
				: CopyAndAddSpin(ref original, key, value);

			static JsonObject CopyAndAddSpin(ref JsonObject original, string key, JsonValue? value)
			{
				var spinner = new SpinWait();
				while (true)
				{
					spinner.SpinOnce();
					var snapshot = Volatile.Read(ref original);
					var copy = snapshot.CopyAndAdd(key, value);
					if (ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot)))
					{
						return copy;
					}
				}
			}
		}

		/// <summary>Returns a new read-only copy of this object with an additional item</summary>
		/// <param name="key">Name of the field to add. If a field with the same name already exists, the method will return <see langword="false"/>.</param>
		/// <param name="value">Value of the new item</param>
		/// <param name="copy">Receives a new instance with the same content of the original object, plus the additional item</param>
		/// <returns><see langword="true"/> if the field was added, or <see langword="false"/> if there was already a field with the same name.</returns>
		/// <remarks>
		/// <para>If the object was not-readonly, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already-readonly objects, and with read-only values.</para>
		/// </remarks>
		[Pure, MustUseReturnValue]
		public bool TryCopyAndAdd(string key, JsonValue? value, [MaybeNullWhen(false)] out JsonObject copy)
		{
			if (m_items.ContainsKey(key))
			{
				copy = null;
				return false;
			}

			// copy and add the new value
			var items = new Dictionary<string, JsonValue>(m_items);
			items.Add(key, value?.ToReadOnly() ?? JsonNull.Null);

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			copy = new (items, readOnly: true);
			return true;
		}

		/// <summary>Returns a new read-only copy of this object, with an additional field</summary>
		/// <param name="key">Name of the field to set. If a field with the same name already exists, its previous value will be overwritten.</param>
		/// <param name="value">Value of the new field</param>
		/// <returns>A new instance with the same content of the original object, plus the additional item</returns>
		/// <remarks>
		/// <para>If a field with the same name already exists, its value will be overwritten.</para>
		/// <para>If the object was not-readonly, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already-readonly objects, and with read-only values.</para>
		/// </remarks>
		[Pure, MustUseReturnValue]
		public JsonObject CopyAndSet(string key, JsonValue? value)
		{
			// copy and set the new value
			var items = new Dictionary<string, JsonValue>(m_items);
			items[key] = value?.ToReadOnly() ?? JsonNull.Null;

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			return new(items, readOnly: true);
		}

		/// <summary>Replaces a published JSON Object with a new version with an added field, in a thread-safe manner, using a <see cref="SpinWait"/> if necessary.</summary>
		/// <param name="original">Reference to the currently published JSON Object</param>
		/// <param name="key">Name of the field to set. If a field with the same name already exists, its previous value will be overwritten.</param>
		/// <param name="value">Value of the field.</param>
		/// <returns>New published JSON Object, that includes the new field.</returns>
		/// <remarks>
		/// <para>This method will attempt to atomically replace the original JSON Object with a new version, unless another thread was able to update it faster, in which case it will simply retry with the newest version, until it is able to successfully update the reference.</para>
		/// <para>Caution: the order of operation between threads is not guaranteed, and this method _may_ loop infinitely if it is perpetually blocked by another, faster, thread !</para>
		/// </remarks>
		public static JsonObject CopyAndSet(ref JsonObject original, string key, JsonValue? value)
		{
			var snapshot = Volatile.Read(ref original);
			var copy = snapshot.CopyAndSet(key, value);

			return ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot))
				? copy
				: CopyAndSetSpin(ref original, key, value);

			static JsonObject CopyAndSetSpin(ref JsonObject original, string key, JsonValue? value)
			{
				var spinner = new SpinWait();
				while (true)
				{
					spinner.SpinOnce();
					var snapshot = Volatile.Read(ref original);
					var copy = snapshot.CopyAndSet(key, value);
					if (ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot)))
					{
						return copy;
					}
				}
			}
		}

		/// <summary>Returns a new read-only copy of this object, with an additional field</summary>
		/// <param name="key">Name of the new field</param>
		/// <param name="value">Value of the new field</param>
		/// <param name="previous">If the field was already present, receives its previous value. If not, receives <see langword="null"/>.</param>
		/// <returns>A new instance with the same content of the original object, plus the additional item</returns>
		/// <remarks>
		/// <para>If a field with the same name already exists, its value will be overwritten and the previous value will be stored in <see cref="previous"/>.</para>
		/// <para>If the object was not-readonly, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already-readonly objects, and with read-only values.</para>
		/// </remarks>
		[Pure, MustUseReturnValue]
		public JsonObject CopyAndSet(string key, JsonValue? value, out JsonValue? previous)
		{
			var items = new Dictionary<string, JsonValue>(m_items);

			// get the previous value if it exists
			items.TryGetValue(key, out previous);
			// set the new value
			items[key] = value?.ToReadOnly() ?? JsonNull.Null;

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			return new(items, readOnly: true);
		}

		/// <summary>Returns a new read-only copy of this object without the specifield item</summary>
		/// <param name="key">Name of the field to remove from the copy</param>
		/// <returns>A new instance with the same content of the original object, but with the specified item removed.</returns>
		/// <remarks>
		/// <para>If the object was not read-only, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already read-only objects.</para>
		/// </remarks>
		public JsonObject CopyAndRemove(string key)
		{
			var items = m_items;
			if (!items.ContainsKey(key))
			{ // the key does not exist so there will be no changes
				return m_readOnly ? this : ToReadOnly();
			}

			if (items.Count == 1)
			{ // we already now key is contained in the object, so if it's the only one, the object will become empty.
				return EmptyReadOnly;
			}

			// copy and remove
			items = new(items);
			items.Remove(key);

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			return new(items, readOnly: true);
		}

		/// <summary>Returns a new read-only copy of this object without the specifield item</summary>
		/// <param name="key">Name of the field to remove from the copy</param>
		/// <param name="previous"></param>
		/// <returns>A new instance with the same content of the original object, but with the specified item removed.</returns>
		/// <remarks>
		/// <para>If the object was not read-only, existing non-readonly fields will also be converted to read-only.</para>
		/// <para>For best performances, this should only be used on already read-only objects.</para>
		/// </remarks>
		public JsonObject CopyAndRemove(string key, out JsonValue? previous)
		{
			var items = m_items;
			if (!items.TryGetValue(key, out previous))
			{ // the key does not exist so there will be no changes
				return m_readOnly ? this : ToReadOnly();
			}

			if (items.Count == 1)
			{ // we already now key is contained in the object, so if it's the only one, the object will become empty.
				return EmptyReadOnly;
			}

			// copy and remove
			items = new(items);
			items.Remove(key);

			if (!m_readOnly)
			{ // some existing items may not be readonly, we may have to convert them as well
				MakeReadOnly(items);
			}

			return new(items, readOnly: true);
		}

		/// <summary>Replaces a published JSON Object with a new version without the specified field, in a thread-safe manner, using a <see cref="SpinWait"/> if necessary.</summary>
		/// <param name="original">Reference to the currently published JSON Object</param>
		/// <param name="key">Name of the field to remove. If the field was not present, the object will not be changed.</param>
		/// <returns>New published JSON Object without the field, or the original object if the was not present.</returns>
		/// <remarks>
		/// <para>This method will attempt to atomically replace the original JSON Object with a new version, unless another thread was able to update it faster, in which case it will simply retry with the newest version, until it is able to successfully update the reference.</para>
		/// <para>Caution: the order of operation between threads is not guaranteed, and this method _may_ loop infinitely if it is perpetually blocked by another, faster, thread !</para>
		/// </remarks>
		public static JsonObject CopyAndRemove(ref JsonObject original, string key)
		{
			var snapshot = Volatile.Read(ref original);
			var copy = snapshot.CopyAndRemove(key);
			if (ReferenceEquals(copy, snapshot))
			{ // the field did not exist
				return snapshot;
			}

			return ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot))
				? copy
				: CopyAndRemoveSpin(ref original, key);

			static JsonObject CopyAndRemoveSpin(ref JsonObject original, string key)
			{
				var spinner = new SpinWait();
				while (true)
				{
					spinner.SpinOnce();
					var snapshot = Volatile.Read(ref original);
					var copy = snapshot.CopyAndRemove(key);
					if (ReferenceEquals(snapshot, Interlocked.CompareExchange(ref original, copy, snapshot)))
					{
						return copy;
					}
				}
			}
		}

		#endregion

		#region AddRange...

		#region Mutable...

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange(ReadOnlySpan<KeyValuePair<string, JsonValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Length));

			foreach (var item in items)
			{
				Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
				// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
				self.Add(item.Key, item.Value ?? JsonNull.Null);
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange(ReadOnlySpan<(string Key, JsonValue? Value)> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Length));

			foreach (var item in items)
			{
				Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
				self.Add(item.Key, item.Value ?? JsonNull.Null);
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(ReadOnlySpan<(string Key, TValue Value)> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Length));

			foreach (var item in items)
			{
				Contract.Debug.Requires(item.Key != null);
				self.Add(item.Key, FromValue<TValue>(item.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange(KeyValuePair<string, JsonValue>[] items)
		{
			Contract.NotNull(items);
			return AddRange(items.AsSpan());
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange((string, JsonValue?)[] items)
		{
			Contract.NotNull(items);
			return AddRange(items.AsSpan());
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange(IDictionary<string, JsonValue> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			if (items.Count == 0)
			{
				return this;
			}

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Count));

			switch (items)
			{
				case JsonObject obj:
				{
					foreach (var item in obj.m_items)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						self.Add(item.Key, item.Value);
					}

					break;
				}
				case Dictionary<string, JsonValue> dict:
				{
					foreach (var item in dict)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, item.Value ?? JsonNull.Null);
					}

					break;
				}
				case FrozenDictionary<string, JsonValue> dict:
				{
					foreach (var item in dict)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, item.Value ?? JsonNull.Null);
					}

					break;
				}
				case ImmutableDictionary<string, JsonValue> immu:
				{
					foreach (var item in immu)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, item.Value ?? JsonNull.Null);
					}

					break;
				}
				default:
				{
					foreach (var item in items)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, item.Value ?? JsonNull.Null);
					}

					break;
				}
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
#if NET9_0_OR_GREATER
		[OverloadResolutionPriority(-1)]
#endif
		public JsonObject AddRange(IEnumerable<KeyValuePair<string, JsonValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			switch (items)
			{
				case IDictionary<string, JsonValue> dict:
				{
					return AddRange(dict);
				}
				case KeyValuePair<string, JsonValue>[] arr:
				{
					return AddRange(arr.AsSpan());
				}
				case List<KeyValuePair<string, JsonValue>> list:
				{
					return AddRange(CollectionsMarshal.AsSpan(list));
				}
				default:
				{
					return AddRangeSlow(this, items);
				}
			}

			[MethodImpl(MethodImplOptions.NoInlining)]
			static JsonObject AddRangeSlow(JsonObject obj, IEnumerable<KeyValuePair<string, JsonValue>> items)
			{
				var self = obj.m_items;
				if (items.TryGetNonEnumeratedCount(out var count))
				{
					if (count == 0) return obj;
					self.EnsureCapacity(unchecked(self.Count + count));
				}

				foreach (var item in items)
				{
					Contract.Debug.Requires(item.Key != null && !ReferenceEquals(obj, item.Value));
					// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
					self.Add(item.Key, item.Value ?? JsonNull.Null);
				}

				return obj;
			}

		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRange(IEnumerable<(string Key, JsonValue Value)> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			switch (items)
			{
				case (string, JsonValue?)[] arr:
				{
					return AddRange(arr.AsSpan());
				}
				case List<(string, JsonValue?)> list:
				{
					return AddRange(CollectionsMarshal.AsSpan(list));
				}
				default:
				{
					return AddRangeSlow(this, items);
				}
			}

			[MethodImpl(MethodImplOptions.NoInlining)]
			static JsonObject AddRangeSlow(JsonObject obj, IEnumerable<(string Key, JsonValue Value)> items)
			{
				var self = obj.m_items;
				if (items.TryGetNonEnumeratedCount(out var count))
				{
					if (count == 0) return obj;
					self.EnsureCapacity(unchecked(self.Count + count));
				}

				foreach (var item in items)
				{
					Contract.Debug.Requires(item.Key != null && !ReferenceEquals(obj, item.Value));
					// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
					self.Add(item.Key, item.Value ?? JsonNull.Null);
				}

				return obj;
			}
		}

		#endregion

		#region Immutable...

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(ReadOnlySpan<KeyValuePair<string, JsonValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Length));

			foreach (var item in items)
			{
				Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
				// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
				self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(ReadOnlySpan<(string Key, JsonValue? Value)> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(unchecked(self.Count + items.Length));

			foreach (var item in items)
			{
				Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
				// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
				self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(KeyValuePair<string, JsonValue>[] items)
		{
			Contract.NotNull(items);
			return AddRangeReadOnly(items.AsSpan());
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly((string Key, JsonValue? Value)[] items)
		{
			Contract.NotNull(items);
			return AddRangeReadOnly(items.AsSpan());
		}

		/// <summary>Add multiple fields, converting them read-only if required</summary>
		/// <param name="items">Set of elements to add. If some values are mutable, a read-only copy will be added instead.</param>
		/// <returns>Same instance (for chaining)</returns>
		/// <remarks>
		/// <para>Fields that already exist will be overwritten.</para>
		/// <para>For performance reasons, added JSON Objects or Arrays should already be read-only, otherwise a deep-copy will be performed.</para></remarks>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(IDictionary<string, JsonValue> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (items.Count == 0) return this;

			var self = m_items;

			self.EnsureCapacity(unchecked(self.Count + items.Count));

			switch (items)
			{
				case JsonObject obj:
				{
					if (obj.IsReadOnly)
					{
						// we assume that the values are already guaranteed to be read-only, so we can skip the ToReadOnly() call!
						foreach (var item in obj.m_items)
						{
							Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value) && item.Value.IsReadOnly);
							self.Add(item.Key, item.Value);
						}
					}
					else
					{
						foreach (var item in obj.m_items)
						{
							Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
							self.Add(item.Key, item.Value.ToReadOnly());
						}
					}
					break;
				}
				case Dictionary<string, JsonValue> dict:
				{
					foreach (var item in dict)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}
					break;
				}
				case FrozenDictionary<string, JsonValue> dict:
				{
					foreach (var item in dict)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}
					break;
				}
				case ImmutableDictionary<string, JsonValue> dict:
				{
					foreach (var item in dict)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}
					break;
				}
				default:
				{
					foreach (var item in items)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}
					break;
				}
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(IEnumerable<KeyValuePair<string, JsonValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			switch (items)
			{
				case IDictionary<string, JsonValue> dict:
				{
					return AddRangeReadOnly(dict);
				}
				case KeyValuePair<string, JsonValue>[] arr:
				{
					return AddRangeReadOnly(arr.AsSpan());
				}
				case List<KeyValuePair<string, JsonValue>> list:
				{
					return AddRangeReadOnly(CollectionsMarshal.AsSpan(list));
				}
				default:
				{
					var self = m_items;
					if (items.TryGetNonEnumeratedCount(out var count))
					{
						if (count == 0) return this;
						self.EnsureCapacity(unchecked(self.Count + count));
					}

					foreach (var item in items)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}

					return this;
				}
			}
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddRangeReadOnly(IEnumerable<(string Key, JsonValue Value)> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			switch (items)
			{
				case (string, JsonValue?)[] arr:
				{
					return AddRangeReadOnly(arr.AsSpan());
				}
				case List<(string, JsonValue?)> list:
				{
					return AddRangeReadOnly(CollectionsMarshal.AsSpan(list));
				}
				default:
				{
					var self = m_items;
					if (items.TryGetNonEnumeratedCount(out var count))
					{
						if (count == 0) return this;
						self.EnsureCapacity(unchecked(self.Count + count));
					}

					foreach (var item in items)
					{
						Contract.Debug.Requires(item.Key != null && !ReferenceEquals(this, item.Value));
						// ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
						self.Add(item.Key, (item.Value ?? JsonNull.Null).ToReadOnly());
					}

					return this;
				}
			}
		}

		#endregion

		#endregion

		#region AddValues [of T] ...

		#region Mutable...

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Length));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValue(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(KeyValuePair<string, TValue>[] items)
		{
			Contract.NotNull(items);
			return AddValues<TValue>(items.AsSpan());
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(Dictionary<string, TValue> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			if (items.Count == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Count));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValue(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(List<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			if (items.Count == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Count));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValue(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValues<TValue>(IEnumerable<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			switch (items)
			{
				case Dictionary<string, TValue> dict:
				{
					return AddValues(dict);
				}
				case List<KeyValuePair<string, TValue>> list:
				{
					return AddValues<TValue>(CollectionsMarshal.AsSpan(list));
				}
				case KeyValuePair<string, TValue>[] arr:
				{
					return AddValues<TValue>(arr.AsSpan());
				}
				default:
				{
					var self = m_items;
					if (items.TryGetNonEnumeratedCount(out var count))
					{
						if (count == 0) return this;
						self.EnsureCapacity(checked(this.Count + count));
					}

					foreach (var kvp in items)
					{
						self.Add(kvp.Key, FromValue(kvp.Value));
					}

					return this;
				}
			}
		}

		#endregion

		#region Immutable...

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValuesReadOnly<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			if (items.Length == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Length));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValueReadOnly(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValuesReadOnly<TValue>(KeyValuePair<string, TValue>[] items)
		{
			Contract.NotNull(items);
			return AddValuesReadOnly<TValue>(items.AsSpan());
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValuesReadOnly<TValue>(Dictionary<string, TValue> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			if (items.Count == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Count));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValueReadOnly(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValuesReadOnly<TValue>(List<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			if (items.Count == 0) return this;

			var self = m_items;
			self.EnsureCapacity(checked(this.Count + items.Count));

			foreach (var kvp in items)
			{
				self.Add(kvp.Key, FromValueReadOnly(kvp.Value));
			}

			return this;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public JsonObject AddValuesReadOnly<TValue>(IEnumerable<KeyValuePair<string, TValue>> items)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.NotNull(items);

			switch (items)
			{
				case Dictionary<string, TValue> dict:
				{
					return AddValuesReadOnly(dict);
				}
				case List<KeyValuePair<string, TValue>> list:
				{
					return AddValuesReadOnly<TValue>(CollectionsMarshal.AsSpan(list));
				}
				case KeyValuePair<string, TValue>[] arr:
				{
					return AddValuesReadOnly<TValue>(arr.AsSpan());
				}
				default:
				{
					var self = m_items;
					if (items.TryGetNonEnumeratedCount(out var count))
					{
						if (count == 0) return this;
						self.EnsureCapacity(checked(this.Count + count));
					}

					foreach (var kvp in items)
					{
						self.Add(kvp.Key, FromValueReadOnly(kvp.Value));
					}

					return this;
				}
			}
		}

		#endregion

		#endregion

		/// <summary>Removes the value with the specified key from this object.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool Remove(string key)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.Debug.Requires(key != null);
			return m_items.Remove(key);
		}

		/// <summary>Removes the value with the specified key from this object.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool Remove(ReadOnlySpan<char> key)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().Remove(key);
#else
			// we have to allocate the string here :(
			return m_items.Remove(key.ToString());
#endif
		}

		/// <summary>Removes the value with the specified key from this object.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool Remove(ReadOnlyMemory<char> key)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().Remove(key.Span);
#else
			// we may have to allocate the string here :(
			return m_items.Remove(key.GetStringOrCopy());
#endif
		}

		/// <summary>Removes the value with the specified key from this object, and copies the element to the <paramref name="value" /> parameter.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <param name="value">The removed element.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public bool Remove(string key, [MaybeNullWhen(false)] out JsonValue value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			return m_items.Remove(key, out value);
		}

		/// <summary>Removes the value with the specified key from this object, and copies the element to the <paramref name="value" /> parameter.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <param name="value">The removed element.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public bool Remove(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out JsonValue value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
#if NET9_0_OR_GREATER
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().Remove(key, out _, out value);
#else
			// we have to allocate the string here :(
			return m_items.Remove(key.ToString(), out value);
#endif
		}

#if NET9_0_OR_GREATER

		/// <summary>Removes the value with the specified key from this object, and copies the element to the <paramref name="value" /> parameter.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <param name="actualKey">The removed key.</param>
		/// <param name="value">The removed element.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public bool Remove(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out string actualKey, [MaybeNullWhen(false)] out JsonValue value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().Remove(key, out actualKey, out value);
		}

		/// <summary>Removes the value with the specified key from this object, and copies the element to the <paramref name="value" /> parameter.</summary>
		/// <param name="key">The key of the element to remove.</param>
		/// <param name="actualKey">The removed key.</param>
		/// <param name="value">The removed element.</param>
		/// <exception cref="T:System.ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
		/// <returns><see langword="true" /> if the element is successfully found and removed; otherwise, <see langword="false" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public bool Remove(ReadOnlyMemory<char> key, [MaybeNullWhen(false)] out string actualKey, [MaybeNullWhen(false)] out JsonValue value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			return m_items.GetAlternateLookup<ReadOnlySpan<char>>().Remove(key.Span, out actualKey, out value);
		}

#endif

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public bool Remove(KeyValuePair<string, JsonValue> keyValuePair)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			Contract.Debug.Requires(keyValuePair.Key != null);
			if (!m_items.TryGetValue(keyValuePair.Key, out var prev) || !prev.Equals(keyValuePair.Value))
			{
				return false;
			}
			return m_items.Remove(keyValuePair.Key);
		}

		[EditorBrowsable(EditorBrowsableState.Always)]
		public void Clear()
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			m_items.Clear();
		}

		/// <summary>Ensures that the dictionary can hold up to a specified number of entries without any further expansion of its backing storage.</summary>
		/// <param name="capacity">The number of entries.</param>
		/// <exception cref="T:System.ArgumentOutOfRangeException">
		/// <paramref name="capacity" /> is less than 0.</exception>
		/// <returns>The current capacity of the <see cref="T:System.Collections.Generic.Dictionary`2" />.</returns>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public int EnsureCapacity(int capacity) => m_items.EnsureCapacity(capacity);

		/// <summary>Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries.</summary>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public void TrimExcess() => this.TrimExcess(this.Count);

		/// <summary>Sets the capacity of this dictionary to hold up a specified number of entries without any further expansion of its backing storage.</summary>
		/// <param name="capacity">The new capacity.</param>
		/// <exception cref="T:System.ArgumentOutOfRangeException">
		/// <paramref name="capacity" /> is less than <see cref="Count" />.</exception>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public void TrimExcess(int capacity) => m_items.TrimExcess(capacity);

		#region Public Properties...

		/// <summary>Type d'objet JSON</summary>
		public override JsonType Type => JsonType.Object;

		/// <summary>Indique s'il s'agit de la valeur par défaut du type ("vide")</summary>
		public override bool IsDefault => this.Count == 0;

		/// <summary>Indique si l'objet contient des valeurs</summary>
		public bool HasValues => this.Count > 0;

		/// <summary>Retourne la valeur de l'attribut "__class", ou null si absent (ou pas une chaine)</summary>
		public string? CustomClassName => Get<string?>(JsonTokens.CustomClassAttribute, null);

		#endregion

		#region Getters...

		[ContractAnnotation("required:true => notnull")]
		private TJson? InternalGet<TJson>(JsonType expectedType, string key, bool required)
			where TJson : JsonValue
		{
			if (!m_items.TryGetValue(key, out var value) || value is JsonNull)
			{ // The property does not exist in this object, or is null or missing
				if (required) JsonValueExtensions.FailFieldIsNullOrMissing(this, key);
				return null;
			}
			if (value.Type != expectedType)
			{ // The property exists, but is not of the expected type ??
				throw Error_ExistingKeyTypeMismatch(key, value, expectedType);
			}
			return (TJson) value;
		}

		[Pure, MethodImpl(MethodImplOptions.NoInlining)]
		private static ArgumentException Error_ExistingKeyTypeMismatch(string key, JsonValue value, JsonType expectedType) => new($"The specified key '{key}' exists, but is a {value.Type} instead of expected {expectedType}", nameof(key));

		/// <summary>Tests if the object contains the <paramref name="key"/> property.</summary>
		/// <param name="key">Name of the property</param>
		/// <returns>Returns <see langword="true" /> if the entry is present; otherwise, <see langword="false" /></returns>
		/// <remarks>Please note that this will return <see langword="true" /> even if the property value is null. To treat <c>null</c> the same as missing, please use <see cref="Has(string)"/> instead.</remarks>
		/// <example><code>
		/// { Foo: "..." }.Has("Foo") => true
		/// { Foo: ""    }.Has("Foo") => true  // empty string
		/// { Foo: null  }.Has("Foo") => true  // explicit null
		/// { Bar: "..." }.Has("Foo") => false // not found
		/// </code></example>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool ContainsKey(string key) => m_items.ContainsKey(key);

		bool ICollection<KeyValuePair<string, JsonValue>>.Contains(KeyValuePair<string, JsonValue> keyValuePair) => ((ICollection<KeyValuePair<string, JsonValue>>)m_items).Contains(keyValuePair);

		/// <summary>Tests if the object contains the <paramref name="key"/> property, and that its value is not <c>null</c></summary>
		/// <param name="key">Name of the property</param>
		/// <returns>Returns <see langword="true" /> if the entry is present and not <see cref="JsonNull.Null"/> or <see cref="JsonNull.Missing"/>.</returns>
		/// <example><code>
		/// { "Foo": "..." }.Has("Foo") => true
		/// { "Foo": ""    }.Has("Foo") => true  // empty string is not 'null'
		/// { "Foo": null  }.Has("Foo") => false // found but explicit null
		/// { "Bar": "..." }.Has("Foo") => false // not found
		/// </code></example>
		[EditorBrowsable(EditorBrowsableState.Always)]
		public bool Has(string key) => m_items.TryGetValue(key, out var value) && !value.IsNullOrMissing();

		/// <inheritdoc/>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Always)]
		public override JsonValue GetValue(string key) => m_items.GetValueOrDefault(key).RequiredField(key);

		/// <inheritdoc/>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Always)]
		public override JsonValue GetValueOrDefault(string key, JsonValue? missingValue = null) => m_items.TryGetValue(key, out var value) ? value : (missingValue ?? JsonNull.Missing);

		/// <inheritdoc/>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Always)]
		public override JsonValue GetValueOrDefault(ReadOnlySpan<char> key, JsonValue? missingValue = null) => TryGetValue(key, out var value) ? value : (missingValue ?? JsonNull.Missing);

		/// <inheritdoc/>
		[Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
		[EditorBrowsable(EditorBrowsableState.Always)]
		public override JsonValue GetValueOrDefault(ReadOnlyMemory<char> key, JsonValue? missingValue = null) => TryGetValue(key, out var value) ? value : (missingValue ?? JsonNull.Missing);

		/// <summary>Returns a JSON Object at the given path, or create a new empty object if missing</summary>
		/// <param name="path"><see cref="JsonPath">path</see> to the object</param>
		/// <returns>Existing object, or a new empty object.</returns>
		/// <example><code>
		/// { }.GetOrCreateObject("foo").Set("bar", 123) => { "foo": { "bar": 123 } }
		/// { }.GetOrCreateObject("foo.bar").Set("baz", 123) => { "foo": { "bar": { "baz": 123 } } }
		/// </code></example>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		/// <exception cref="System.ArgumentNullException">If <paramref name="path"/> is <see langword="null"/> or empty</exception>
		/// <exception cref="System.ArgumentException">If any traversed node in the path is of an incompatible path. For example with <c>"foo[1].bar.baz"</c> if either <c>foo</c> is not an array, or <c>bar</c> is not an object</exception>
		public JsonObject GetOrCreateObject(string path)
		{
			JsonPath.ThrowIfEmpty(path);
			return (JsonObject) SetPathInternal(JsonPath.Create(path), null, JsonType.Object);
		}

		/// <summary>Returns a JSON Object at the given path, or create a new empty object if missing</summary>
		/// <param name="path"><see cref="JsonPath">path</see> to the object</param>
		/// <returns>Existing object, or a new empty object.</returns>
		/// <example><code>
		/// { }.GetOrCreateObject("foo").Set("bar", 123) => { "foo": { "bar": 123 } }
		/// { }.GetOrCreateObject("foo.bar").Set("baz", 123) => { "foo": { "bar": { "baz": 123 } } }
		/// </code></example>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		/// <exception cref="System.ArgumentNullException">If <paramref name="path"/> is <see langword="null"/> or empty</exception>
		/// <exception cref="System.ArgumentException">If any traversed node in the path is of an incompatible path. For example with <c>"foo[1].bar.baz"</c> if either <c>foo</c> is not an array, or <c>bar</c> is not an object</exception>
		public JsonObject GetOrCreateObject(JsonPath path)
		{
			JsonPath.ThrowIfEmpty(path);
			return (JsonObject) SetPathInternal(path, null, JsonType.Object);
		}

		/// <summary>Returns a JSON Array at the given path, or create a new empty array if missing</summary>
		/// <param name="path"><see cref="JsonPath">path</see> to the array</param>
		/// <returns>Existing array, or a new empty array.</returns>
		/// <example><code>
		/// { }.GetOrCreateArray("foo").Set(0, "bar") => { "foo": [ "bar" ] }
		/// { }.GetOrCreateArray("foo.bar").Set(0, "baz") => { "foo": { "bar": [ "baz" ] } }
		/// </code></example>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		/// <exception cref="System.ArgumentNullException">If <paramref name="path"/> is <see langword="null"/> or empty</exception>
		/// <exception cref="System.ArgumentException">If any traversed node in the path is of an incompatible path. For example with <c>"foo[1].bar.baz"</c> if either <c>foo</c> is not an array, or <c>bar</c> is not an object</exception>
		public JsonArray GetOrCreateArray(string path)
		{
			JsonPath.ThrowIfEmpty(path);
			return (JsonArray) SetPathInternal(JsonPath.Create(path), null, JsonType.Array);
		}

		/// <summary>Returns a JSON Array at the given path, or create a new empty array if missing</summary>
		/// <param name="path"><see cref="JsonPath">path</see> to the array</param>
		/// <returns>Existing array, or a new empty array.</returns>
		/// <example><code>
		/// { }.GetOrCreateArray("foo").Set(0, "bar") => { "foo": [ "bar" ] }
		/// { }.GetOrCreateArray("foo.bar").Set(0, "baz") => { "foo": { "bar": [ "baz" ] } }
		/// </code></example>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		/// <exception cref="System.ArgumentNullException">If <paramref name="path"/> is <see langword="null"/> or empty</exception>
		/// <exception cref="System.ArgumentException">If any traversed node in the path is of an incompatible path. For example with <c>"foo[1].bar.baz"</c> if either <c>foo</c> is not an array, or <c>bar</c> is not an object</exception>
		public JsonArray GetOrCreateArray(JsonPath path)
		{
			JsonPath.ThrowIfEmpty(path);
			return (JsonArray) SetPathInternal(path, null, JsonType.Array);
		}

		/// <summary>Sets the value at the given path</summary>
		/// <param name="path"><see cref="JsonPath">path</see> of the value to set.</param>
		/// <param name="value">New value</param>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		public void SetPath(string path, JsonValue? value)
		{
			JsonPath.ThrowIfEmpty(path);
			SetPathInternal(JsonPath.Create(path), value ?? JsonNull.Null);
		}

		/// <summary>Sets the value at the given path</summary>
		/// <param name="path"><see cref="JsonPath">path</see> of the value to set.</param>
		/// <param name="value">New value</param>
		/// <remarks>If any intermediate element in the traversed path is missing, it will be created as required (either as an object or an array)</remarks>
		public void SetPath(JsonPath path, JsonValue? value)
		{
			JsonPath.ThrowIfEmpty(path);
			SetPathInternal(path, value ?? JsonNull.Null);
		}

		private JsonValue SetPathInternal(JsonPath path, JsonValue? valueToSet, JsonType? expected = null)
		{
			JsonValue current = this;
			JsonValue? prevNode = null;
			ReadOnlyMemory<char> prevKey = default;
			Index prevIndex = default;
			foreach (var (parent, key, idx, last) in path)
			{
				if (key.Length > 0)
				{ // field access

					// we have foo.bar, but foo was missing, so current = missing, key = "bar"
					// => we have to Set("foo", {}) on the parent... which itself must be an object

					if (current is not JsonObject obj)
					{
						if (!current.IsNullOrMissing())
						{ // incompatible type!
							throw ThrowHelper.InvalidOperationException($"Cannot set key '{key.ToString()}' because parent '{parent}' is not an object");
						}

						if (prevNode == null)
						{
							throw ThrowHelper.InvalidOperationException("Cannot update a null root object");
						}

						// we need to create the parent object first
						obj = JsonObject.Create();
						// and assign it to it's parent
						if (prevKey.Length > 0)
						{
							if (prevNode is not JsonObject prevObj)
							{
								throw ThrowHelper.InvalidOperationException($"Cannot set key '{key.ToString()}' because parent of '{parent}' is not an object");
							}
							prevObj.Set(prevKey, obj);
						}
						else
						{
							if (prevNode is not JsonArray prevArray)
							{
								throw ThrowHelper.InvalidOperationException($"Cannot set key '{key.ToString()}' because parent of '{parent}' is not an array");
							}
							prevArray.Set(prevIndex, obj);
						}
					}

					if (last)
					{ // the last token is a field access
						if (valueToSet == null)
						{ // we need to return the value or create it if required

							var actual = obj.GetValueOrDefault(key);

							if (expected == JsonType.Null)
							{ // means "delete"
								obj.Remove(key);
								return actual;
							}
							if (actual.IsNullOrMissing())
							{
								actual = expected == JsonType.Object ? JsonObject.Create() : expected == JsonType.Array ? JsonArray.Create() : throw new ArgumentException(nameof(expected));
								obj.Set(key, actual);
							}
							else if (actual.Type != expected)
							{
								throw new InvalidOperationException($"The specified key '{key}' exists, but is of type {actual.Type} instead of expected {expected}");
							}
							return actual;
						}
						else
						{ // we need to set the value
							obj.Set(key, valueToSet);
							return valueToSet;
						}
					}

					// we need to continue
					prevNode = obj;
					current = obj.GetValueOrDefault(key);
					prevKey = key;
					prevIndex = default;
				}
				else
				{ // array index
					
					if (current is not JsonArray arr)
					{
						if (!current.IsNullOrMissing())
						{ // incompatible type!
							throw ThrowHelper.InvalidOperationException($"Cannot set index {idx} because parent '{parent}' is not an array");
						}
						if (prevNode == null)
						{
							throw ThrowHelper.InvalidOperationException("Cannot update a null root array");
						}

						// we need to create the parent array first
						arr = JsonArray.Create();
						// and assign it to it's parent
						if (prevKey.Length > 0)
						{
							if (prevNode is not JsonObject prevObj)
							{
								throw ThrowHelper.InvalidOperationException($"Cannot set index {idx} because parent of '{parent}' is not an object");
							}
							prevObj.Set(prevKey, arr);
						}
						else
						{
							if (prevNode is not JsonArray prevArray)
							{
								throw ThrowHelper.InvalidOperationException($"Cannot set index {idx} because parent of '{parent}' is not an array");
							}
							prevArray.Set(prevIndex, arr);
						}
					}

					if (last)
					{ // the last token is an array index
						if (valueToSet == null)
						{ // we need to return the value or create it if required

							var actual = arr[idx];

							if (expected == JsonType.Null)
							{ // means "delete"
								arr.RemoveAt(idx);
								return actual;
							}

							if (actual.IsNullOrMissing())
							{
								actual = expected == JsonType.Object ? JsonObject.Create() : expected == JsonType.Array ? JsonArray.Create() : throw new ArgumentException(nameof(expected));
								arr[idx] = actual;
							}
							else if (actual.Type != expected)
							{
								throw new InvalidOperationException($"The specified index '{idx}' exists, but is of type {actual.Type} instead of expected {expected}");
							}
							return actual;
						}
						else
						{ // we need to set the value
							arr.Set(idx, valueToSet);
							return valueToSet;
						}
					}

					prevNode = arr;
					current = arr.GetValueOrDefault(idx);
					prevKey = default;
					prevIndex = idx;
				}
			}

			// we should not end up here!
			throw new InvalidOperationException();
		}

		/// <summary>Removes the value at the given path</summary>
		/// <param name="path"><see cref="JsonPath">path</see> of the value to remove.</param>
		/// <returns><see langword="true"/> if the value was found and was removed, or <see langword="false"/> if it was no present.</returns>
		/// <example>
		/// <c>{ "foo": { "bar": 123, "baz": 456 } }.RemovePath("foo.bar") => { "foo": { "baz": 456 } }</c>
		/// </example>
		public bool RemovePath(string path)
		{
			JsonPath.ThrowIfEmpty(path);
			return !SetPathInternal(JsonPath.Create(path), null, JsonType.Null).IsNullOrMissing();
		}

		/// <summary>Removes the value at the given path</summary>
		/// <param name="path"><see cref="JsonPath">path</see> of the value to remove.</param>
		/// <returns><see langword="true"/> if the value was found and was removed, or <see langword="false"/> if it was no present.</returns>
		/// <example>
		/// <c>{ "foo": { "bar": 123, "baz": 456 } }.RemovePath("foo.bar") => { "foo": { "baz": 456 } }</c>
		/// </example>
		public bool RemovePath(JsonPath path)
		{
			JsonPath.ThrowIfEmpty(path);
			return !SetPathInternal(path, null, JsonType.Null).IsNullOrMissing();
		}

		#endregion

		#region Setters...

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set<TValue>(string key, TValue? value)
		{
			m_items[key] = FromValue(value);
			return this;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set<TValue>(ReadOnlySpan<char> key, TValue? value)
		{
#if NET9_0_OR_GREATER
			var items = m_items.GetAlternateLookup<ReadOnlySpan<char>>();
			items[key] = FromValue(value);
#else
			m_items[key.ToString()] = FromValue(value);
#endif
			return this;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set<TValue>(ReadOnlyMemory<char> key, TValue? value)
		{
			// if we can get the original string, we won't need to allocate
			if (key.TryGetString(out var k))
			{
				m_items[k] = FromValue(value);
				return this;
			}

#if NET9_0_OR_GREATER
			// won't allocate if the key already exists
			var items = m_items.GetAlternateLookup<ReadOnlySpan<char>>();
			items[key.Span] = FromValue(value);
#else
			// we need to allocate in all cases, even if the key already exists
			m_items[key.ToString()] = FromValue(value);
#endif
			return this;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set(string key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			m_items[key] = value ?? JsonNull.Null;
			return this;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set(ReadOnlySpan<char> key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

#if NET9_0_OR_GREATER
			var items = m_items.GetAlternateLookup<ReadOnlySpan<char>>();
			//note: this will not allocate if the key already exists
			items[key] = value ?? JsonNull.Null;
#else
			m_items[key.ToString()] = value ?? JsonNull.Null;
#endif
			return this;
		}

		[CollectionAccess(CollectionAccessType.UpdatedContent)]
		public JsonObject Set(ReadOnlyMemory<char> key, JsonValue? value)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			if (key.TryGetString(out var k))
			{
				m_items[k] = value ?? JsonNull.Null;
				return this;
			}
#if NET9_0_OR_GREATER
			var items = m_items.GetAlternateLookup<ReadOnlySpan<char>>();
			//note: this will not allocate if the key already exists
			items[key.Span] = value ?? JsonNull.Null;
#else
			m_items[key.Span.ToString()] = value ?? JsonNull.Null;
#endif
			return this;
		}

		/// <summary>Adds the "_class" attribute with the resolved type id</summary>
		/// <typeparam name="TContainer">Type that must be resolved</typeparam>
		/// <param name="resolver">Optional custom resolver used to bind the value into a managed type.</param>
		public JsonObject SetClassId<TContainer>(ICrystalJsonTypeResolver? resolver = null)
		{
			return SetClassId(typeof(TContainer), resolver);
		}

		/// <summary>Adds the "_class" attribute with the resolved type id</summary>
		/// <param name="type">Type that must be resolved</param>
		/// <param name="resolver">Optional custom resolver used to bind the value into a managed type.</param>
		public JsonObject SetClassId(Type type, ICrystalJsonTypeResolver? resolver = null)
		{
			Contract.NotNull(type);
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);

			var typeDef = (resolver ?? CrystalJson.DefaultResolver).ResolveJsonType(type) ?? throw CrystalJson.Errors.Serialization_CouldNotResolveTypeDefinition(type);
			this.ClassId = typeDef.ClassId;
			return this;
		}

		public string? ClassId
		{
			get => this[JsonTokens.CustomClassAttribute].ToStringOrDefault();
			set
			{
				if (string.IsNullOrEmpty(value))
				{
					Remove(JsonTokens.CustomClassAttribute);
				}
				else
				{
					this[JsonTokens.CustomClassAttribute] = value;
				}
			}
		}

		#endregion

		#region Merging...

		/// <summary>Copy the fields of an object onto the current object</summary>
		/// <param name="other">Object that will be merged with the current instance.</param>
		/// <param name="deepCopy">If <see langword="false"/> (default), copy the content of <paramref name="other"/> as-is; otherwise, clone all the elements before merging them.</param>
		/// <param name="keepNull">If <see langword="false"/> (default), fields set to null in <paramref name="other"/> will be removed; otherwise, they will be kept as null entries in the merged result.</param>
		public void MergeWith(JsonObject other, bool deepCopy = false, bool keepNull = false)
		{
			Merge(this, other, deepCopy, keepNull);
		}

		/// <summary>Copy the fields of an object onto the another object</summary>
		/// <param name="parent">Object that will be modified</param>
		/// <param name="other">Object that will be copied to the parent.</param>
		/// <param name="deepCopy">If <see langword="false"/> (default), copy the content of <paramref name="other"/> as-is; otherwise, clone all the elements before merging them.</param>
		/// <param name="keepNull">If <see langword="false"/> (default), fields set to null in <paramref name="other"/> will be removed; otherwise, they will be kept as null entries in the merged result.</param>
		public static JsonObject Merge(JsonObject parent, JsonObject? other, bool deepCopy = false, bool keepNull = false)
		{
			Contract.NotNull(parent);

			// cannot mutate a read-only object!
			if (parent.IsReadOnly) throw FailCannotMutateReadOnlyValue(parent);

			if (other is not null && other.Count > 0)
			{
				// recursively merge all properties:
				// - Copy the items from 'other', optionally merging them if they already exist in 'parent'
				// - If the new value is null or missing:
				//   - it will be set to null iif keepNull is true and the new value is an explicit null
				//   - otherwise, it will be removed (keepNull is false, or the new value is JsonNull.Missing)
				// - Merging is only supported between two objects or two arrays
				// - In all other cases, the value in 'other' will overwrite the previous value

				foreach (var (k, v) in other)
				{
					if (!parent.TryGetValue(k, out var mine))
					{
						mine = JsonNull.Missing;
					}

					switch ((mine, v))
					{
						case (JsonObject a, JsonObject b):
						{ // merge two objects together
							Merge(a, b, deepCopy, keepNull);
							break;
						}
						case (JsonArray a, JsonArray b):
						{ // merge two arrays together
							JsonArray.Merge(a, b, deepCopy, keepNull);
							break;
						}
						case (_, JsonNull n):
						{ // remove value (or set to null)
							if (!keepNull || !ReferenceEquals(n, JsonNull.Null))
							{
								parent.Remove(k);
							}
							else
							{
								parent[k] = JsonNull.Null;
							}
							break;
						}
						default:
						{ // overwrite previous value
							parent[k] = deepCopy ? v.Copy() : v;
							break;
						}
					}
				}
			}
			return parent;
		}

		public JsonObject ComputePatch(JsonObject after, bool deepCopy = false)
		{
			//note: we already know that there is a difference
			var patch = new JsonObject();

			var items = m_items;

			// mark for deletion any keys that are missing from 'after'
			foreach (var k in items.Keys)
			{
				if (!after.ContainsKey(k))
				{ // use explicit null to trigger a deletion when the patch is applied later
					patch[k] = JsonNull.Null;
				}
			}

			// add/update any new  keys
			foreach (var (k, v) in after)
			{
				if (!items.TryGetValue(k, out var p))
				{
					// add
					if (!v.IsNullOrMissing())
					{
						patch[k] = deepCopy ? v.Copy() : v;
					}
				}
				else if (!p.Equals(v))
				{ // update
					switch (p, v)
					{
						case (JsonObject a, JsonObject b):
						{
							patch[k] = a.ComputePatch(b, deepCopy);
							break;
						}
						case (JsonArray a, JsonArray b):
						{
							patch[k] = a.ComputePatch(b, deepCopy);
							break;
						}
						case (_, JsonNull):
						{ // use explicit null to trigger a deletion when the patch is applied later
							patch[k] = JsonNull.Null;
							break;
						}
						default:
						{
							patch[k] = deepCopy ? v.Copy() : v;
							break;
						}
					}
				}
			}

			return patch;
		}

		/// <summary>Apply a patch to the object (in place)</summary>
		/// <param name="patch">Object that will be copied to the parent.</param>
		/// <param name="deepCopy"></param>
		public void ApplyPatch(JsonObject patch, bool deepCopy = false)
		{
			if (m_readOnly) throw FailCannotMutateImmutableValue(this);

			// recursively merge all properties:
			// - Copy the items from 'other', optionally merging them if they already exist in 'parent'
			// - If the new value is null or missing:
			//   - it will be set to null iif keepNull is true and the new value is an explicit null
			//   - otherwise, it will be removed (keepNull is false, or the new value is JsonNull.Missing)
			// - Merging is only supported between two objects or two arrays
			// - In all other cases, the value in 'other' will overwrite the previous value

			var items = m_items;

			foreach (var (k, v) in patch)
			{
				if (!items.TryGetValue(k, out var mine))
				{
					mine = JsonNull.Missing;
				}

				switch ((mine, v))
				{
					case (JsonObject a, JsonObject b):
					{ // merge two objects together
						if (a.IsReadOnly)
						{
							a = a.ToMutable();
							items[k] = a;
						}
						a.ApplyPatch(b, deepCopy);
						break;
					}
					case (JsonArray a, JsonObject b) when (b.ContainsKey("__patch")):
					{ // merge two arrays (using the patch-object "form")
						if (a.IsReadOnly)
						{
							a = a.ToMutable();
							items[k] = a;
						}
						a.ApplyPatch(b, deepCopy);
						break;
					}
					case (_, JsonNull):
					{ // remove value (or set to null)
						items.Remove(k);
						break;
					}
					default:
					{ // overwrite previous value
						items[k] = deepCopy ? v.Copy() : v;
						break;
					}
				}
			}
		}

		#endregion

		#region Projection...

		/// <summary>Génère un Picker en cache, capable d'extraire une liste de champs d'objet JSON</summary>
		public static Func<JsonObject, JsonObject> CreatePicker(ReadOnlySpan<string> fields, bool removeFromSource = false)
		{
			var projections = CheckProjectionFields(fields, keepMissing: false);
			return (obj) => Project(obj, projections, removeFromSource);
		}

		/// <summary>Génère un Picker en cache, capable d'extraire une liste de champs d'objet JSON</summary>
		public static Func<JsonObject, JsonObject> CreatePicker(IEnumerable<string> fields, bool keepMissing, bool removeFromSource = false)
		{
			var projections = CheckProjectionFields(fields as string[] ?? fields.ToArray(), keepMissing);
			return (obj) => Project(obj, projections, removeFromSource);
		}

		/// <summary>Génère un Picker en cache, capable d'extraire une liste de champs d'objet JSON</summary>
		public static Func<JsonObject, JsonObject> CreatePicker(IDictionary<string, JsonValue?> defaults, bool removeFromSource = false)
		{
			var projections = CheckProjectionDefaults(defaults);
			return (obj) => Project(obj, projections, removeFromSource);
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="false"/>, any field missing from the object will be omitted in the result. If <see langword="true"/>, they will be present but with a <see cref="JsonNull.Missing"/> value</param>
		/// <returns>New object that only contains the values of the fields specified in <paramref name="fields"/></returns>
		public JsonObject Pick(ReadOnlySpan<string> fields, bool keepMissing = false)
		{
			return Project(this, CheckProjectionFields(fields, keepMissing));
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="false"/>, any field missing from the object will be omitted in the result. If <see langword="true"/>, they will be present but with a <see cref="JsonNull.Missing"/> value</param>
		/// <returns>New object that only contains the values of the fields specified in <paramref name="fields"/></returns>
		public JsonObject Pick(string[] fields, bool keepMissing = false)
		{
			return Project(this, CheckProjectionFields(fields, keepMissing));
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="false"/>, any field missing from the object will be omitted in the result. If <see langword="true"/>, they will be present but with a <see cref="JsonNull.Missing"/> value</param>
		/// <returns>New object that only contains the values of the fields specified in <paramref name="fields"/></returns>
		public JsonObject Pick(IEnumerable<string> fields, bool keepMissing = false)
		{
			return Project(this, CheckProjectionFields(fields as string[] ?? fields.ToArray(), keepMissing));
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="false"/>, any field missing from the object will be omitted in the result. If <see langword="true"/>, they will be present but with a <see cref="JsonNull.Missing"/> value</param>
		/// <returns>New object that only contains the values of the fields specified in <paramref name="fields"/></returns>
		public JsonObject Project(ReadOnlySpan<(string Name, JsonPath Path, JsonValue? Fallback)> fields, bool keepMissing = false)
		{
			return Project(this, fields);
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="false"/>, any field missing from the object will be omitted in the result. If <see langword="true"/>, they will be present but with a <see cref="JsonNull.Missing"/> value</param>
		/// <returns>New object that only contains the values of the fields specified in <paramref name="fields"/></returns>
		public JsonObject Project(ReadOnlySpan<(string Name, JsonPath Path)> fields, bool keepMissing = false)
		{
			return Project(this, CheckProjectionFields(fields, keepMissing));
		}

		/// <summary>Retourne un nouvel objet ne contenant que certains champs spécifiques de cet objet</summary>
		/// <param name="defaults">Liste des des champs à conserver, avec une éventuelle valeur par défaut</param>
		/// <returns>Nouvel objet qui ne contient que les champs spécifiés dans <paramref name="defaults"/></returns>
		public JsonObject PickFrom(IDictionary<string, JsonValue?> defaults)
		{
			return Project(this, CheckProjectionDefaults(defaults));
		}

		/// <summary>Retourne un nouvel objet ne contenant que certains champs spécifiques de cet objet</summary>
		/// <param name="defaults">Liste des des champs à conserver, avec une éventuelle valeur par défaut</param>
		/// <returns>Nouvel objet qui ne contient que les champs spécifiés dans <paramref name="defaults"/></returns>
		public JsonObject PickFrom(object defaults)
		{
			return Project(this, CheckProjectionDefaults(defaults));
		}

		/// <summary>Vérifie que la liste de champs de projection ne contient pas de null, empty ou doublons</summary>
		/// <param name="keys">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="true"/>, any field missing from the object will be present with value <see cref="JsonNull.Missing"/>; otherwise, they will be omitted.</param>
		[ContractAnnotation("keys:null => halt")]
		internal static KeyValuePair<string, JsonValue?>[] CheckProjectionFields(ReadOnlySpan<string> keys, bool keepMissing)
		{
			var res = new KeyValuePair<string, JsonValue?>[keys.Length];
			var set = new HashSet<string>();
			int p = 0;

			foreach (var key in keys)
			{
				if (string.IsNullOrEmpty(key))
				{
					throw ThrowHelper.InvalidOperationException($"Cannot project empty or null field name: [{string.Join(", ", keys.ToArray())}]");
				}
				set.Add(key);
				res[p++] = new(key, keepMissing ? JsonNull.Missing : null);
			}
			if (set.Count != keys.Length)
			{
				throw ThrowHelper.InvalidOperationException($"Cannot project duplicate field name: [{string.Join(", ", keys.ToArray())}]");
			}

			return res;
		}

		/// <summary>Vérifie que la liste de champs de projection ne contient pas de null, empty ou doublons</summary>
		/// <param name="fields">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMissing">If <see langword="true"/>, any field missing from the object will be present with value <see cref="JsonNull.Missing"/>; otherwise, they will be omitted.</param>
		internal static (string Name, JsonPath Path, JsonValue? Fallback)[] CheckProjectionFields(ReadOnlySpan<(string Name, JsonPath Path)> fields, bool keepMissing)
		{
			var res = new (string, JsonPath, JsonValue?)[fields.Length];
			var set = new HashSet<string>(StringComparer.Ordinal);
			int p = 0;

			foreach (var field in fields)
			{
				if (string.IsNullOrEmpty(field.Name))
				{
					throw ThrowHelper.InvalidOperationException($"Cannot project empty or null field name: [{string.Join(", ", fields.ToArray())}]");
				}
				if (field.Path.IsEmpty())
				{
					throw ThrowHelper.InvalidOperationException($"Cannot project empty field path: [{string.Join(", ", fields.ToArray())}]");
				}
				set.Add(field.Name);
				res[p++] = (field.Name, field.Path, keepMissing ? JsonNull.Missing : null);
			}
			if (set.Count != fields.Length)
			{
				throw ThrowHelper.InvalidOperationException($"Cannot project duplicate field name: [{string.Join(", ", fields.ToArray())}]");
			}

			return res;
		}

		/// <summary>Vérifie que la liste de champs de projection ne contient pas de null, empty ou doublons</summary>
		/// <param name="defaults">Liste des clés à projeter, avec leur valeur par défaut</param>
		/// <remarks>Si un champ est manquant dans l'objet source, la valeur par défaut est utilisée, sauf si elle est égale à null.</remarks>
		[ContractAnnotation("defaults:null => halt")]
		internal static KeyValuePair<string, JsonValue?>[] CheckProjectionDefaults(IDictionary<string, JsonValue?> defaults)
		{
			Contract.NotNull(defaults);

			var res = new KeyValuePair<string, JsonValue?>[defaults.Count];
			var set = new HashSet<string>();
			int p = 0;

			foreach(var kvp in defaults)
			{
				if (string.IsNullOrEmpty(kvp.Key))
				{
					ThrowHelper.ThrowInvalidOperationException($"Cannot project empty or null field name: [{string.Join(", ", defaults.Select(x => x.Key))}]");
				}

				set.Add(kvp.Key);
				res[p++] = kvp;
			}

			if (set.Count != defaults.Count)
			{
				ThrowHelper.ThrowInvalidOperationException($"Cannot project duplicate field name: [{string.Join(", ", defaults.Select(x => x.Key))}]");
			}

			return res;
		}

		[ContractAnnotation("defaults:null => halt")]
		internal static KeyValuePair<string, JsonValue?>[] CheckProjectionDefaults(object defaults)
		{
			Contract.NotNull(defaults);

			var obj = FromObjectReadOnly(defaults);
			Contract.Debug.Assert(obj != null);
			//note: garantit sans doublons et sans clés vides
			return obj.ToArray()!;
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="item">Source JSON object</param>
		/// <param name="defaults">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="removeFromSource">If <see langword="true"/>, any projected field will be removed from the source. If <see langword="false"/>, they will be copied into the resulting object</param>
		/// <param name="keepMutable">If <see langword="false"/>, the created object will be marked as read-only if the source is already read-only; otherwise, it will be mutable.</param>
		/// <returns>New object that contains the selected fields from the source, or their default values.</returns>
		/// <remarks><code>
		/// { "A": 1, "C": false }.Project({ "A": 0, "B": 42, "C": true}) => { "A": 1, "B": 42, "C": false }
		/// </code></remarks>
		internal static JsonObject Project(JsonObject item, ReadOnlySpan<KeyValuePair<string, JsonValue?>> defaults, bool removeFromSource = false, bool keepMutable = false)
		{
			Contract.Debug.Requires(item != null);

			if (removeFromSource && item.IsReadOnly)
			{
				throw new InvalidOperationException("Cannot remove picked fields from a read-only source");
			}

			var obj = new JsonObject(defaults.Length, item.Comparer);
			foreach (var prop in defaults)
			{
				if (item.TryGetValue(prop.Key, out var value))
				{
					obj[prop.Key] = value;
					if (removeFromSource)
					{
						item.Remove(prop.Key);
					}
				}
				else if (prop.Value != null)
				{
					obj[prop.Key] = prop.Value;
				}
			}

			// keep the "readonly-ness" of the original, unless specified otherwise
			if (item.IsReadOnly && !keepMutable)
			{
				obj.FreezeUnsafe();
			}

			return obj;
		}

		/// <summary>Returns a new object that only contains the specified fields of this instance</summary>
		/// <param name="item">Source JSON object</param>
		/// <param name="defaults">List of the names of the fields to keep, each with a default value if they are missing from the source</param>
		/// <param name="keepMutable">If <see langword="false"/>, the created object will be marked as read-only if the source is already read-only; otherwise, it will be mutable.</param>
		/// <returns>New object that contains the selected fields from the source, or their default values.</returns>
		internal static JsonObject Project(JsonObject item, ReadOnlySpan<(string Name, JsonPath Path, JsonValue? Fallback)> defaults, bool keepMutable = false)
		{
			Contract.Debug.Requires(item != null);

			var obj = new JsonObject(defaults.Length, item.Comparer);
			foreach (var prop in defaults)
			{
				if (item.TryGetPathValue(prop.Path, out var value))
				{
					obj[prop.Name] = value;
				}
				else if (prop.Fallback != null)
				{
					obj[prop.Name] = prop.Fallback;
				}
			}

			// keep the "readonly-ness" of the original, unless specified otherwise
			if (item.IsReadOnly && !keepMutable)
			{
				obj.FreezeUnsafe();
			}

			return obj;
		}

		#endregion

		#region Filtering...

		/// <summary>Returns a new object that only includes the fields of the original that passed a given filter</summary>
		/// <param name="value">Original object</param>
		/// <param name="filter">Filter that is called for each field of the original. If the filter returns <see langword="false"/>, for a field, it will not be copied into the result.</param>
		/// <param name="deepCopy">If <see langword="true"/>, performs a deep copy of the fields that pass the filter. If <see langword="false"/>, copy them by reference. Has no effect for fields that are already read-only.</param>
		/// <returns>New object with only the fields that passed the filter.</returns>
		/// <remarks>If all fields are discarded, the returned object will be empty.</remarks>
		internal static JsonObject Without(JsonObject value, Func<string, bool> filter, bool deepCopy)
		{
			Contract.Debug.Requires(value != null && filter != null);

			var obj = new JsonObject(value.Count, value.Comparer);
			foreach(var item in value)
			{
				if (!filter(item.Key))
				{
					obj[item.Key] = deepCopy ? item.Value.Copy() :  item.Value;
				}
			}
			return obj;
		}

		/// <summary>Returns a new object with only the fields of the original whose names are present in a given list</summary>
		/// <param name="value">Original object</param>
		/// <param name="filtered">List of names of fields that must be copied. Any field not present in this set will be discarded.</param>
		/// <param name="deepCopy">If <see langword="true"/>, performs a deep copy of the fields that pass the filter. If <see langword="false"/>, copy them by reference. Has no effect for fields that are already read-only.</param>
		/// <returns>New object with only the fields that passed the filter.</returns>
		/// <remarks>If all fields are discarded, the returned object will be empty.</remarks>
		internal static JsonObject Without(JsonObject value, HashSet<string> filtered, bool deepCopy)
		{
			Contract.Debug.Requires(value != null && filtered != null);

			var obj = new JsonObject(value.Count, value.Comparer);
			foreach (var item in value)
			{
				if (!filtered.Contains(item.Key))
				{
					obj[item.Key] = deepCopy ? item.Value.Copy() : item.Value;
				}
			}
			return obj;
		}

		/// <summary>Returns a new object without the specified field from the original</summary>
		/// <param name="value">Original object</param>
		/// <param name="field">Name of the field that must be ommited.</param>
		/// <param name="deepCopy">If <see langword="true"/>, performs a deep copy of the fields that pass the filter. If <see langword="false"/>, copy them by reference. Has no effect for fields that are already read-only.</param>
		/// <returns>New object with all the fields of the original, except the one with the same name as <paramref name="field"/>.</returns>
		/// <remarks>If all fields are discarded, the returned object will be empty.</remarks>
		internal static JsonObject Without(JsonObject value, string field, bool deepCopy)
		{
			Contract.Debug.Requires(value != null && field != null);

			var obj = Copy(value, deepCopy, readOnly: false);
			obj.Remove(field);
			return obj;
		}

		/// <summary>Returns a new object that only includes the fields of the original that passed a given filter</summary>
		/// <param name="filter">Filter that is called for each field of the object. If the filter returns <see langword="false"/>, for a field, it will not be copied into the result.</param>
		/// <param name="deepCopy">If <see langword="true"/>, performs a deep copy of the fields that pass the filter. If <see langword="false"/>, copy them by reference. Has no effect for fields that are already read-only.</param>
		/// <returns>New object with only the fields that passed the filter.</returns>
		public JsonObject Without(Func<string, bool> filter, bool deepCopy = false)
		{
			Contract.NotNull(filter);
			return Without(this, filter, deepCopy);
		}

		/// <summary>Returns a new object, without the specified field</summary>
		/// <param name="fieldToRemove">Name of the field that should be omitted, if present.</param>
		/// <param name="deepCopy">If <see langword="true"/>, performs a deep copy of the fields that pass the filter. If <see langword="false"/>, copy them by reference. Has no effect for fields that are already read-only.</param>
		/// <returns>New object that does not exclude the specified field.</returns>
		public JsonObject Without(string fieldToRemove, bool deepCopy = false)
		{
			Contract.NotNullOrEmpty(fieldToRemove);
			return Without(this, fieldToRemove, deepCopy);
		}

		/// <summary>Remove a field from this object</summary>
		/// <param name="fieldToRemove">Name of the field to remove</param>
		/// <returns>The same object, but with the field removed (if it was present)</returns>
		/// <remarks>This method is identical to <see cref="Remove(string)"/>, be can be chained with another call</remarks>
		public JsonObject Erase(string fieldToRemove)
		{
			Contract.NotNullOrEmpty(fieldToRemove);
			this.Remove(fieldToRemove);
			return this;
		}

		#endregion

		#region Sorting...

		private static bool TrySortValue(JsonValue item, IComparer<string> comparer, [MaybeNullWhen(false)] out JsonValue result)
		{
			result = null!;

			switch (item)
			{
				case JsonObject obj:
				{
					if (TrySortByKeys(obj.m_items, comparer, out var subItems))
					{
						result = new JsonObject(subItems, obj.m_readOnly);
						return true;
					}

					return false;
				}
				case JsonArray arr:
				{
					// only allocate the buffer if at least one child has changed
					JsonValue[]? items = null;
					for (int i = 0; i < arr.Count; i++)
					{
						if (TrySortValue(arr[i], comparer, out var val))
						{
							(items ??= arr.ToArray())[i] = val;
						}
					}

					if (items != null)
					{ // at least one change
						result = new JsonArray(items, items.Length, arr.IsReadOnly);
						return true;
					}

					return false;
				}
				default:
				{
					return false;
				}
			}
		}

		/// <summary>Order the keys of a dictionary, using the specified comparer</summary>
		/// <returns><see langword="false"/> if the object was already ordered, or <see langword="true"/> if the object has been changed to re-order the keys.</returns>
		/// <remarks>This is used to guarantee that serializing a JSON object produces the same text or bytes, preserving equality and checksum.</remarks>
		private static bool TrySortByKeys(Dictionary<string, JsonValue> items, IComparer<string> comparer, [MaybeNullWhen(false)] out Dictionary<string, JsonValue> result)
		{
			//ATTENTION: this assumes that currently (as of .NET 8) a Dictionary<TKey,TValue> preserves the insertion order of keys, as long as there are no deletions, meaning that enumerating the Dictionary will yield the same order.
			// => it is unlikely that this will ever change, because it would break a lot of code. But if this happens, we will need to find a different solution!

			Contract.Debug.Requires(items != null && comparer != null);
			result = null!;

			int count = items.Count;
			if (count == 0)
			{ // nothing to do
				return false;
			}

			bool changed = false;

			// each value needs to be sorted recursively
			var valuesArray = ArrayPool<JsonValue>.Shared.Rent(count);
			items.Values.CopyTo(valuesArray, 0);
			var values = valuesArray.AsSpan(0, count);

			for (int i = 0; i < values.Length; i++)
			{
				if (TrySortValue(values[i], comparer, out var val))
				{
					values[i] = val;
					changed = true;
				}
			}

			// order by the keys
			var keysArray = ArrayPool<string>.Shared.Rent(count);
			items.Keys.CopyTo(keysArray, 0);
			var keys = keysArray.AsSpan(0, count);

			var indexesArray = ArrayPool<int>.Shared.Rent(count);
			var indexes = indexesArray.AsSpan(0, count);
			for (int i = 0; i < indexes.Length; i++)
			{
				indexes[i] = i;
			}

			keys.Sort(indexes, comparer);

			if (!changed)
			{
				// If all keys where already ordered, the array of indexes will stay ordered as well [0, 1, 2, ..., N-1].
				// => in this case, we don't have to create a copy, and simply need to return the same instance
				for (int i = 0; i < indexes.Length; i++)
				{
					if (indexes[i] != i)
					{ // there was at least one swap!
						changed = true;
						break;
					}
				}
			}

			if (changed)
			{ // order was changed, need to create a new object
				result = new Dictionary<string, JsonValue>(keys.Length, items.Comparer);
				for (int i = 0; i < keys.Length; i++)
				{
					result[keys[i]] = values[indexes[i]];
				}
			}

			// return the buffers to the pool
			indexes.Clear();
			ArrayPool<int>.Shared.Return(indexesArray);
			keys.Clear();
			ArrayPool<string>.Shared.Return(keysArray);
			values.Clear();
			ArrayPool<JsonValue>.Shared.Return(valuesArray);

			return changed;
		}

		/// <summary>Order the keys of this object</summary>
		/// <param name="comparer">Optional key comparer, or <see cref="StringComparer.Ordinal"/> if omitted.</param>
		public void SortKeys(IComparer<string>? comparer = null)
		{
			if (m_readOnly) throw FailCannotMutateReadOnlyValue(this);
			if (TrySortByKeys(m_items, comparer ?? StringComparer.Ordinal, out var items))
			{
				m_items.Clear();
				foreach (var kvp in items)
				{
					m_items[kvp.Key] = kvp.Value;
				}
			}
		}

		/// <summary>Returns a new JSON Object, with the same content as the original, but with the keys sorted</summary>
		/// <param name="map">Original JSON Object. This object will not be modified.</param>
		/// <param name="comparer">Optional key comparer, or <see cref="StringComparer.Ordinal"/> if omitted.</param>
		/// <returns>Shallow copy of the object, with the keys sorted using the specified key comparer</returns>
		public static JsonObject OrderedByKeys(JsonObject map, IComparer<string>? comparer = null)
		{
			Contract.NotNull(map);

			if (TrySortByKeys(map.m_items, comparer ?? StringComparer.Ordinal, out var items))
			{
				return new JsonObject(items, map.m_readOnly);
			}

			//TODO: to copy or not to copy?
			return map;
		}

		/// <summary>Returns a copy of this JSON Object, with the same content as the original, but with the keys sorted</summary>
		public JsonObject OrderedByKeys(IComparer<string>? comparer = null)
		{
			return OrderedByKeys(this, comparer);
		}

		#endregion

		#region Conversion...

		internal override bool IsSmallValue()
		{
			const int LARGE_OBJECT = 5;
			if (m_items.Count >= LARGE_OBJECT)
			{
				return false;
			}

			foreach(var v in m_items.Values)
			{
				if (v.IsSmallValue())
				{
					return false;
				}
			}

			return true;
		}

		internal override bool IsInlinable() => false;

		private string GetMutabilityDebugLiteral() => m_readOnly ? " ReadOnly" : "";

		internal override string GetCompactRepresentation(int depth)
		{
			const int MAX_ITEMS = 4;

			if (m_items.Count == 0) return "{ }"; // empty

			// We will output up to 4 fields.
			// If the value of a field is "small" it is written entierly. If not, it will be replaced with '...'

			var sb = new StringBuilder("{ ");
			int i = 0;
			foreach(var kv in m_items)
			{
				if (i >= MAX_ITEMS) { sb.Append($", /* \u2026 {(m_items.Count - MAX_ITEMS):N0} more */"); break; }
				if (i > 0) sb.Append(", ");

				sb.Append(kv.Key).Append(": ");
				if (depth == 0 || kv.Value.IsSmallValue())
				{ 
					sb.Append(kv.Value.GetCompactRepresentation(depth + 1));
				}
				else
				{
					switch (kv.Value.Type)
					{
						case JsonType.Object: sb.Append("{\u2026}"); break;
						case JsonType.Array: sb.Append("[\u2026]"); break;
						case JsonType.String: sb.Append("\"\u2026\""); break;
						default: sb.Append('\u2026'); break;
					}
				}
				i++;
			}
			sb.Append(" }");
			return sb.ToString();
		}

		/// <summary>Converts this JSON Object into a <see cref="Dictionary{TKey,TValue}">Dictionary&lt;string, object?&gt;</see>.</summary>
		[Pure]
		[EditorBrowsable(EditorBrowsableState.Never)]
		public override object? ToObject()
		{
			return CrystalJsonParser.DeserializeCustomClassOrStruct(this, typeof(object), CrystalJson.DefaultResolver);
		}

		public override TValue? Bind<TValue>(TValue? defaultValue = default, ICrystalJsonTypeResolver? resolver = null) where TValue : default
		{
			var res = (resolver ?? CrystalJson.DefaultResolver).BindJsonObject(typeof(TValue), this);
			if (res is null)
			{
				return default(TValue) is null && (typeof(TValue) == typeof(JsonValue) || typeof(TValue) == typeof(JsonNull))
					? (defaultValue ?? (TValue?) (object?) JsonNull.Null)
					: defaultValue;
			}
			return (TValue?) res;
		}

		public override object? Bind(Type? type, ICrystalJsonTypeResolver? resolver = null)
		{
			return (resolver ?? CrystalJson.DefaultResolver).BindJsonObject(type, this);
		}

		#endregion

		#region IJsonSerializable

		public override void JsonSerialize(CrystalJsonWriter writer)
		{
			var state = writer.BeginObject();
			foreach (var item in this)
			{
				// first check if the value is not a discarded null or default
				if (!writer.WillBeDiscarded(item.Value))
				{
					//note: the key may require escaping!
					writer.WriteNameEscaped(item.Key);
					item.Value.JsonSerialize(writer);
				}
			}
			writer.EndObject(state);
		}

		/// <inheritdoc />
		public override bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
		{
			//TODO: maybe attempt to do it without allocating?
			// => for the moment, we will serialize the object into memory, and copy the result

			var literal = ToJson();
			if (literal.Length > destination.Length)
			{
				charsWritten = 0;
				return false;
			}

			literal.CopyTo(destination);
			charsWritten = literal.Length;
			return true;
		}

		#endregion

		#region IEquatable<...>

		public override bool Equals(JsonValue? other) => other is JsonObject obj && Equals(obj);

		public bool Equals(JsonObject? other)
		{
			if (other is null || other.Count != this.Count)
			{
				return false;
			}

			var otherItems = other.m_items;
			foreach (var kvp in this)
			{
				if (!otherItems.TryGetValue(kvp.Key, out var o) || !o.Equals(kvp.Value))
				{
					return false;
				}
			}
			return true;
		}

		public bool Equals(JsonObject? other, IEqualityComparer<JsonValue>? comparer)
		{
			if (other is null || other.Count != this.Count)
			{
				return false;
			}
			comparer ??= JsonValueComparer.Default;
			var otherItems = other.m_items;
			foreach (var kvp in this)
			{
				if (!otherItems.TryGetValue(kvp.Key, out var o) || !comparer.Equals(o, kvp.Value))
				{
					return false;
				}
			}
			return true;
		}

		public override int GetHashCode()
		{
			// the hashcode must NEVER change, even if the object is mutated!
			return RuntimeHelpers.GetHashCode(this);
		}

		public override int CompareTo(JsonValue? other)
		{
			throw new NotSupportedException("JSON Object cannot be compared with other elements");
		}

		#endregion

		public ExpandoObject ToExpando()
		{
			var expando = new ExpandoObject();
			var map = (IDictionary<string, object?>) expando;
			foreach (var kvp in m_items)
			{
				map.Add(kvp.Key, kvp.Value.ToObject());
			}
			return expando;
		}

		public KeyValuePair<string, JsonValue>[] ToArray()
		{
			var res = new KeyValuePair<string, JsonValue>[m_items.Count];
			CopyTo(res, 0);
			return res;
		}

		public Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(ICrystalJsonTypeResolver? resolver = null)
			where TKey : notnull
		{
			return (Dictionary<TKey, TValue>) (resolver ?? CrystalJson.DefaultResolver).BindJsonObject(typeof(Dictionary<TKey, TValue>), this)!;
		}

		public void CopyTo(KeyValuePair<string, JsonValue>[] array)
		{
			((ICollection<KeyValuePair<string, JsonValue>>) m_items).CopyTo(array, 0);
		}

		public void CopyTo(KeyValuePair<string, JsonValue>[] array, int arrayIndex)
		{
			((ICollection<KeyValuePair<string, JsonValue>>) m_items).CopyTo(array, arrayIndex);
		}

		public void CopyTo(Span<KeyValuePair<string, JsonValue>> array)
		{
			if (!TryCopyTo(array))
			{
				throw new ArgumentException("Destination is too small");
			}
		}

		public bool TryCopyTo(Span<KeyValuePair<string, JsonValue>> array)
		{
			if (this.m_items.Count > array.Length)
			{
				return false;
			}
			int p = 0;
			foreach (var kv in m_items)
			{
				array[p++] = kv;
			}
			return true;
		}

		public void CopyTo(Span<(string Key, JsonValue Value)> array)
		{
			if (!TryCopyTo(array))
			{
				throw new ArgumentException("Destination is too small");
			}
		}

		public bool TryCopyTo(Span<(string Key, JsonValue Value)> array)
		{
			if (this.m_items.Count > array.Length)
			{
				return false;
			}
			int p = 0;
			foreach (var (k, v) in m_items)
			{
				array[p++] = (k, v);
			}
			return true;
		}

		public override void WriteTo(ref SliceWriter writer)
		{
			writer.WriteByte('{');
			bool first = true;
			foreach (var kv in this)
			{
				// by default, we don't serialize "Missing" values
				if (kv.Value.IsMissing()) break;

				if (first)
				{
					first = false;
				}
				else
				{
					writer.WriteByte(',');
				}

				if (JsonEncoding.NeedsEscaping(kv.Key))
				{
					writer.WriteStringUtf8(JsonEncoding.EncodeSlow(kv.Key));
				}
				else
				{
					writer.WriteByte('"');
					writer.WriteStringUtf8(kv.Key);
					writer.WriteByte('"');
				}
				writer.WriteByte(':');
				kv.Value.WriteTo(ref writer);
			}
			writer.WriteByte('}');
		}

	}

}
