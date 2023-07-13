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

	/// <summary>Resolveur JSON capable d'énumérer les membres d'un type</summary>
	public interface ICrystalJsonTypeResolver : ICrystalTypeResolver
	{
		/// <summary>Inspecte un type pour retrouver la liste de ses membres</summary>
		/// <param name="type">Type à inspecter</param>
		/// <returns>Liste des members compilée, ou null si le type n'est pas compatible (primitive, delegate, ...)</returns>
		/// <remarks>La liste est mise en cache pour la prochaine fois</remarks>
		CrystalJsonTypeDefinition? ResolveJsonType(Type type);

		/// <summary>Bind une valeur JSON en type CLR correspondant (ValueType, Class, List, ...)</summary>
		object? BindJsonValue(Type? type, JsonValue? value);

		/// <summary>Bind un objet JSON en type CLR</summary>
		object? BindJsonObject(Type? type, JsonObject? value);

		/// <summary>Bind un liste JSON en liste d'objets CLR</summary>
		object? BindJsonArray(Type? type, JsonArray? array);

		/// <summary>Bind une valeur JSON en un type CLR spécifique</summary>
		T? BindJson<T>(JsonValue? value);

	}

}