﻿#region Copyright (c) 2005-2023 Doxense SAS
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 	* Redistributions of source code must retain the above copyright
// 	  notice, this list of conditions and the following disclaimer.
// 	* Redistributions in binary form must reproduce the above copyright
// 	  notice, this list of conditions and the following disclaimer in the
// 	  documentation and/or other materials provided with the distribution.
// 	* Neither the name of Doxense nor the
// 	  names of its contributors may be used to endorse or promote products
// 	  derived from this software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL DOXENSE BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

namespace Doxense.Serialization.Json
{
	using System;
	using System.Collections.Generic;
	using Doxense.Diagnostics.Contracts;

	public sealed class JsonValueComparer : IEqualityComparer<JsonValue>, IComparer<JsonValue>, System.Collections.IEqualityComparer, System.Collections.IComparer
	{
		public static readonly JsonValueComparer Default = new JsonValueComparer();

		private JsonValueComparer()
		{ }

		public bool Equals(JsonValue? x, JsonValue? y)
		{
			// il y a pas mal de singletons ou de valeurs interned, donc ca vaut le coup de comparer les pointers
			return object.ReferenceEquals(x, y)
			   || (object.ReferenceEquals(x, null) ? y!.IsNull : x.Equals(y));
		}

		public int GetHashCode(JsonValue? obj)
		{
			return (obj ?? JsonNull.Null).GetHashCode();
		}

		bool System.Collections.IEqualityComparer.Equals(object? x, object? y)
		{
			if (object.ReferenceEquals(x, y)) return true; // catch aussi le null
			var jx = (x as JsonValue) ?? JsonValue.FromValue(x);
			var jy = (y as JsonValue) ?? JsonValue.FromValue(y);
			Contract.Debug.Assert(!object.ReferenceEquals(jx, null) && !object.ReferenceEquals(jy, null));
			return jx.Equals(jy);
		}

		int System.Collections.IEqualityComparer.GetHashCode(object? obj)
		{
			return (obj ?? JsonNull.Null).GetHashCode();
		}

		public int Compare(JsonValue? x, JsonValue? y)
		{
			return object.ReferenceEquals(x, y) ? 0 : (x ?? JsonNull.Null).CompareTo(y);
		}

		int System.Collections.IComparer.Compare(object? x, object? y)
		{
			if (object.ReferenceEquals(x, y)) return 0; // catch aussi le null
			var jx = (x as JsonValue) ?? JsonValue.FromValue(x);
			var jy = (y as JsonValue) ?? JsonValue.FromValue(y);
			Contract.Debug.Assert(!object.ReferenceEquals(jx, null) && !object.ReferenceEquals(jy, null));
			return jx.CompareTo(jy);
		}
	}

}