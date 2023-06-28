﻿#region Copyright Doxense 2005-2020
//
// All rights are reserved. Reproduction or transmission in whole or in part, in
// any form or by any means, electronic, mechanical or otherwise, is prohibited
// without the prior written consent of the copyright owner.
//
#endregion

namespace Doxense.Memory
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;

	/// <summary>Table de mapping bi-directionnelle entre représentation binaire (<see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;<typeparamref name="TRune"/>&gt;</see>) et litérale (<typeparamref name="TLiteral"/>) d'un <typeparamref name="TToken">token</typeparamref></summary>
	/// <typeparam name="TToken">Type du token</typeparam>
	/// <typeparam name="TLiteral">Type de la représentation litérale du token.</typeparam>
	/// <typeparam name="TRune">Element de la réprésentation binaire du token.</typeparam>
	public abstract class TokenMap<TToken, TLiteral, TRune> : IEnumerable<KeyValuePair<TLiteral, TToken>>
		where TLiteral : IEquatable<TLiteral>
		where TRune : struct, IEquatable<TRune>
	{

		/// <summary>Map: byte[] => Token</summary>
		protected TokenDictionary<TToken, TLiteral, TRune> Tokens { get; }

		/// <summary>Map: string => Token </summary>
		protected Dictionary<TLiteral, TToken> Literals { get; }

		protected TokenMap(TokenDictionary<TToken, TLiteral, TRune> tokens, Dictionary<TLiteral, TToken> literals)
		{
			this.Tokens = tokens;
			this.Literals = literals;
		}

		public int Count => this.Literals.Count;

		public IEnumerable<TLiteral> Keys => this.Literals.Keys;

		public IEnumerable<TToken> Values => this.Literals.Values;

		public bool ContainsKey(TLiteral token) => this.Literals.ContainsKey(token);

		public bool ContainsKey(ReadOnlySpan<TRune> token) => this.Tokens.ContainsKey(token);

		/// <summary>Cherche un token à partir de sa représentation binaire</summary>
		public bool TryGetValue(in ReadOnlyMemory<TRune> token, [MaybeNullWhen(false)] out TToken value) => this.Tokens.TryGetValue(token, out value);

		/// <summary>Cherche un token à partir de sa représentation binaire</summary>
		public bool TryGetValue(in ReadOnlySpan<TRune> token, [MaybeNullWhen(false)] out TToken value) => this.Tokens.TryGetValue(token, out value);

		/// <summary>Cherche un token à partir de sa représentation textuelle</summary>
		public bool TryGetValue(TLiteral literal, [MaybeNullWhen(false)] out TToken value) => this.Literals.TryGetValue(literal, out value);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected abstract TLiteral GetLiteral(in ReadOnlyMemory<TRune> token);

		protected abstract ReadOnlyMemory<TRune> GetRunes(TLiteral token);

		public void Add(in ReadOnlyMemory<TRune> token, TToken value)
		{
			var key = GetLiteral(token);
			this.Literals.Add(key, value);
			this.Tokens.Add(token, value);
		}

		public void Add(TLiteral token, TToken value)
		{
			var key = GetRunes(token);
			this.Literals.Add(token, value);
			this.Tokens.Add(key, value);
		}
		
		public Dictionary<TLiteral, TToken>.Enumerator GetEnumerator() => this.Literals.GetEnumerator();

		IEnumerator<KeyValuePair<TLiteral, TToken>> IEnumerable<KeyValuePair<TLiteral, TToken>>.GetEnumerator() => this.Literals.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.Literals.GetEnumerator();

	}

	/// <summary>Table de mapping bi-directionnelle entre représentation en mémoire (<see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;<see cref="char"/>&gt;</see>) et litérale (<see cref="string"/>) d'un <typeparamref name="TToken">token</typeparamref></summary>
	/// <typeparam name="TToken">Type du token</typeparam>
	public class ByteStringTokenMap<TToken> : TokenMap<TToken, string, byte>
	{

		public new ByteStringTokenDictionary<TToken> Tokens => (ByteStringTokenDictionary<TToken>) base.Tokens;
		
		public ByteStringTokenMap() : this(0, Encoding.UTF8)
		{ }

		public ByteStringTokenMap(ByteStringTokenMap<TToken> parent)
			: base(
				new ByteStringTokenDictionary<TToken>(parent.Tokens),
				new Dictionary<string, TToken>(parent.Literals, parent.Literals.Comparer)
			)
		{ }
		
		public ByteStringTokenMap(int capacity, Encoding? encoding = null)
			: base(
				new ByteStringTokenDictionary<TToken>(capacity, encoding),
				new Dictionary<string, TToken>(capacity, StringComparer.Ordinal)
			)
		{ }

		public ByteStringTokenMap(TToken[] items, Func<TToken, string> literal, Encoding? encoding = null)
			: base(
				new ByteStringTokenDictionary<TToken>(items, literal, encoding),
				items.ToDictionary(literal, StringComparer.Ordinal)
			)
		{ }

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Add(Slice token, TToken value)
		{
			Add(token.Memory, value);
		}
		
		protected override string GetLiteral(in ReadOnlyMemory<byte> token)
		{
			if (token.Length == 0)
			{
				return string.Empty;
			}
#if NETFRAMEWORK || NETSTANDARD
			unsafe
			{
				fixed (byte* ptr = token.Span)
				{
					return this.Tokens.Encoding.GetString(ptr, token.Length);
				}
			}
#else
			return this.Tokens.Encoding.GetString(token.Span);
#endif
		}

		protected override ReadOnlyMemory<byte> GetRunes(string token)
		{
			return this.Tokens.Encoding.GetBytes(token).AsMemory();
		}
		
	}

	/// <summary>Table de mapping bi-directionnelle entre représentation encodée (<see cref="ReadOnlySpan{T}">ReadOnlySpan&lt;<see cref="byte"/>&gt;</see>) et litérale (<see cref="string"/>) d'un <typeparamref name="TToken">token</typeparamref></summary>
	/// <typeparam name="TToken">Type du token</typeparam>
	public class CharStringTokenMap<TToken> : TokenMap<TToken, string, char>
	{

		public CharStringTokenMap()
			: base(
				new CharStringTokenDictionary<TToken>(),
				new Dictionary<string, TToken>(StringComparer.Ordinal)
			)
		{ }

		public CharStringTokenMap(TToken[] items, Func<TToken, string> literal)
			: base(
				new CharStringTokenDictionary<TToken>(items, literal),
				items.ToDictionary(literal, StringComparer.Ordinal)
			)
		{ }

		protected override string GetLiteral(in ReadOnlyMemory<char> token)
		{
			return token.ToString();
		}

		protected override ReadOnlyMemory<char> GetRunes(string token)
		{
			return token.AsMemory();
		}
		
	}

}
