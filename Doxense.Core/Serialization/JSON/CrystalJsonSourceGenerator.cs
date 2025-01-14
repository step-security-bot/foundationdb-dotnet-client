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

#if NET9_0_OR_GREATER

namespace Doxense.Serialization.Json
{
	using System.CodeDom.Compiler;
	using System.Diagnostics;
	using System.Diagnostics.CodeAnalysis;
	using System.Globalization;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Text;

	public class CrystalJsonSourceGenerator
	{

		public CrystalJsonTypeResolver JsonResolver { get; set; } = CrystalJson.DefaultResolver;

		public string Namespace { get; set; } = "Snowbank.Serialization.Json.Generated";

		public string SerializerContainerName { get; set; } = "GeneratedSerializers";

		public Dictionary<Type, CrystalJsonTypeDefinition> TypeMap { get; } = [ ];

		public HashSet<Assembly> AllowedAssemblies { get; } = [ ];

		private HashSet<Type> ObservedTypes { get; } = [ ];

		private HashSet<Type> FastFieldSerializable { get; } = ComputeFastFieldSerializable();
		private HashSet<Type> FastFieldArraySerializable { get; } = ComputeFastFieldArraySerializable();

		private static HashSet<Type> ComputeFastFieldSerializable()
		{
			var res = new HashSet<Type>();
			foreach (var m in typeof(CrystalJsonWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (m.Name != nameof(CrystalJsonWriter.WriteField) || m.IsGenericMethod)
				{
					continue;
				}

				var prms = m.GetParameters();
				if (prms.Length != 2) continue;
				if (!prms[0].ParameterType.IsByRef) continue; //HACKHACK: how test a byref type ??

				res.Add(prms[1].ParameterType);
			}

			return res;
		}

		private static HashSet<Type> ComputeFastFieldArraySerializable()
		{
			var res = new HashSet<Type>();
			foreach (var m in typeof(CrystalJsonWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (m.Name != nameof(CrystalJsonWriter.WriteFieldArray) || m.IsGenericMethod)
				{
					continue;
				}

				var prms = m.GetParameters();
				if (prms.Length != 2) continue;
				if (!prms[0].ParameterType.IsByRef) continue; //HACKHACK: how test a byref type ??

				if (prms[1].ParameterType.IsEnumerableType(out var elemType))
				{
					res.Add(elemType);
				}
			}

			return res;
		}

		public void AddType(Type type) => AddTypes([ type ]);

		public void AddTypes(Type[] types) => AddTypes(new ReadOnlySpan<Type>(types));

		public void AddTypes(ReadOnlySpan<Type> types)
		{
			var extra = new Queue<CrystalJsonTypeDefinition>();

			foreach (var type in types)
			{
				if (this.TypeMap.ContainsKey(type)) continue;

				var typeDef = this.JsonResolver.ResolveJsonType(type);
				if (typeDef != null)
				{
					extra.Enqueue(typeDef);
					this.AllowedAssemblies.Add(typeDef.Type.Assembly);
				}
			}

			while(extra.Count > 0)
			{
				var typeDef = extra.Dequeue();
				Console.WriteLine("Inspecting " + typeDef.Type.FullName);
				this.TypeMap.Add(typeDef.Type, typeDef);

				foreach(var nested in typeDef.Type.GetNestedTypes())
				{
					if (!this.AllowedAssemblies.Contains(nested.Assembly)) continue;
					var subDef = this.JsonResolver.ResolveJsonType(nested);
					if (subDef != null && subDef.Members.Length > 0 && this.ObservedTypes.Add(subDef.Type))
					{
						extra.Enqueue(subDef);
					}
				}

				foreach(var member in typeDef.Members)
				{
					if (!this.AllowedAssemblies.Contains(member.Type.Assembly)) continue;

					var subDef = this.JsonResolver.ResolveJsonType(member.Type);

					if (subDef != null && subDef.Members.Length > 0 && this.ObservedTypes.Add(subDef.Type))
					{
						extra.Enqueue(subDef);
					}
				}

			}
		}

		public string GenerateCode()
		{
			var sb = new CodeBuilder();

			sb.Namespace(
				this.Namespace,
				() =>
				{
					sb.Comment("ReSharper disable InconsistentNaming");
					sb.Comment("ReSharper disable PartialTypeWithSinglePart");
					sb.Comment("ReSharper disable RedundantNameQualifier");
					sb.NewLine();

					sb.Class(
						"public partial",
						this.SerializerContainerName,
						[ ],
						[ ],
						() =>
						{
							foreach (var (type, typeDef) in this.TypeMap)
							{
								GenerateCodeForType(sb, typeDef);
							}
						}
					);
				}
			);

			return sb.ToStringAndClear();
		}

		private string GetSerializerName(Type type)
		{
			return type.Name;
		}

		private string GetLocalSerializerRef(Type type)
		{
			return this.SerializerContainerName + "." + GetSerializerName(type);
		}

		private void GenerateCodeForType(CodeBuilder sb, CrystalJsonTypeDefinition typeDef)
		{

			var type = typeDef.Type;
			var jsonSerializerType = typeof(IJsonSerializer<>).MakeGenericType(type);
			var jsonDeserializerForType = typeof(IJsonDeserializerFor<>).MakeGenericType(type);

			var typeName = sb.TypeName(type);
			var serializerName = GetSerializerName(type);
			var serializerTypeName = "_" + serializerName + "JsonSerializer";

			var jsonSerializerInterfaceName = sb.TypeName(jsonSerializerType);
			var jsonDeserializerForInterfaceName = sb.TypeName(jsonDeserializerForType);

			sb.AppendLine($"#region {type.GetFriendlyName()} ...");
			sb.NewLine();

			sb.AppendLine($"/// <summary>Serializer for type <see cref=\"{type.FullName}\">{type.GetFriendlyName()}</see></summary>");
			sb.AppendLine($"public static {serializerTypeName} {serializerName} => m_cached{serializerName} ??= new {serializerTypeName}();");
			sb.NewLine();
			sb.AppendLine($"private static {serializerTypeName}? m_cached{serializerName};");
			sb.NewLine();

			sb.Attribute<DynamicallyAccessedMembersAttribute>([ sb.Constant(DynamicallyAccessedMemberTypes.All) ]);
			sb.Attribute<GeneratedCodeAttribute>([ sb.Constant(nameof(CrystalJsonSourceGenerator)), sb.Constant("0.1") ]);
			sb.Attribute<DebuggerNonUserCodeAttribute>();
			sb.Class(
				"public sealed",
				serializerTypeName,
				[ jsonSerializerInterfaceName, jsonDeserializerForInterfaceName ],
				[ ],
				() =>
				{

					sb.AppendLine("#region Serialization...");
					sb.NewLine();

					#region JsonSerialize...

					// first we need to genereated the cached property names (pre-encoded)
					string encodedPropertyTypeName = sb.TypeName<JsonEncodedPropertyName>();
					foreach (var member in typeDef.Members)
					{
						sb.AppendLine($"private static readonly {encodedPropertyTypeName} _{member.Name} = new({sb.Constant(member.Name)});");
					}
					sb.NewLine();

					sb.Method(
						"public",
						"void",
						nameof(IJsonSerializer<object>.JsonSerialize),
						[
							sb.Parameter<CrystalJsonWriter>("writer"),
							sb.Parameter(typeName + (typeDef.IsNullable ? "?" : ""), "instance")
						],
						() =>
						{

							if (typeDef.IsNullable)
							{
								sb.If(
									"instance is null", () =>
									{
										sb.Call("writer.WriteNull");
										sb.Return();
									}
								);
								sb.NewLine();
							}

							if (!typeDef.IsSealed)
							{
								sb.If(
									$"instance.GetType() != typeof({typeName})", () =>
									{
										sb.Call(sb.TypeName(typeof(CrystalJsonVisitor)) + "." + nameof(CrystalJsonVisitor.VisitValue), [ "instance", $"typeof({typeName})", "writer" ]);
										sb.Return();
									}
								);
								sb.NewLine();
							}

							sb.AppendLine("var state = writer.BeginObject();");

							foreach (var member in typeDef.Members)
							{
								var propertyName = "_" + member.Name;

								sb.NewLine();
								sb.Comment($"{member.Type.GetFriendlyName()} {member.OriginalName} => \"{member.Name}\"");

								if (this.FastFieldSerializable.Contains(member.Type))
								{ // there is a direct, dedicated method for this!
									sb.Comment("fast!");
									sb.Call($"writer.{nameof(CrystalJsonWriter.WriteField)}", [ "in " + propertyName, $"instance.{member.OriginalName}" ]);
									continue;
								}

								if (this.TypeMap.TryGetValue(member.Type, out var subDef))
								{
									sb.Comment("custom!");
									sb.Call($"writer.{nameof(CrystalJsonWriter.WriteField)}", [ "in " + propertyName, $"instance.{member.OriginalName}", GetLocalSerializerRef(subDef.Type) ]);
									continue;
								}

								if (member.Type.IsDictionaryType(out var keyType, out var valueType))
								{
									if (this.TypeMap.ContainsKey(valueType))
									{
										if (keyType == typeof(string))
										{
											sb.Comment("dictionary with string key");
											sb.Call($"writer.{nameof(CrystalJsonWriter.WriteFieldDictionary)}", [ "in " + propertyName, $"instance.{member.OriginalName}", GetLocalSerializerRef(valueType) ]);
											continue;
										}
									}
									sb.Comment($"TODO: unsupported dictionary type: key={keyType.FullName}, value={valueType.FullName}");
								}
								else if (member.Type.IsEnumerableType(out var elemType))
								{
									if (this.FastFieldArraySerializable.Contains(elemType))
									{
										sb.Comment("fast array!");
										sb.Call($"writer.{nameof(CrystalJsonWriter.WriteFieldArray)}", [ "in " + propertyName, $"instance.{member.OriginalName}" ]);
										continue;
									}

									if (this.TypeMap.ContainsKey(elemType))
									{
										sb.Comment("custom array!");
										sb.Call($"writer.{nameof(CrystalJsonWriter.WriteFieldArray)}", [ "in " + propertyName, $"instance.{member.OriginalName}", GetLocalSerializerRef(elemType) ]);
										continue;
									}

									if (member.Type == typeof(IEnumerable<>).MakeGenericType(elemType))
									{
										sb.Comment("custom enumerable!");
										sb.Call($"writer.{nameof(CrystalJsonWriter.WriteFieldArray)}", [ "in " + propertyName, $"instance.{member.OriginalName}" ]);
									}

									sb.Comment("TODO: unsupported enumerable type: " + member.Type.FullName);
								}

								sb.Comment("unknown type");
								sb.Call($"writer.{nameof(CrystalJsonWriter.WriteField)}", [ "in " + propertyName, $"instance.{member.OriginalName}" ]);
							}

							sb.NewLine();
							sb.Call("writer.EndObject", [ "state" ]);
						}
					);

					sb.AppendLine("#endregion");
					sb.NewLine();

					#endregion
					
					#region JsonDeserialize...

					sb.AppendLine("#region Deserialization...");
					sb.NewLine();

					bool isValueType = typeDef.Type.IsValueType;
					bool smallish = false; //typeDef.Members.Length <= 5;

					if (!smallish)
					{
						// we need a set of accessors for each field or property, in order to set readonly or init-only fields

						foreach (var member in typeDef.Members)
						{
							sb.Comment($"{member.OriginalName} {{ get; init; }}");
							sb.Attribute<UnsafeAccessorAttribute>([ sb.Constant(UnsafeAccessorKind.Field) ], [ ("Name", sb.Constant("<" + member.OriginalName + ">k__BackingField")) ]);
							sb.AppendLine($"private static extern ref {sb.TypeName(member.Type)} {member.OriginalName}Accessor({(isValueType ? "ref " : "")}{typeName} instance);");
							sb.NewLine();
						}
					}

					sb.Method(
						"public",
						typeName,
						nameof(IJsonDeserializer<object>.JsonDeserialize),
						[ sb.Parameter<JsonValue>("value"), sb.Parameter<ICrystalJsonTypeResolver>("resolver", nullable: true) ],
						() =>
						{

							if (smallish)
							{
								// var obj = value.AsObject();
								// return new TValue()
								// {
								//    Foo = obj.Get<TValue.Foo>("foo", default),
								//    Bar = value.Get<TValue.Bar>("bar", default),
								//    ...
								// }

								sb.AppendLine("var obj = value.AsObject();");
								sb.AppendLine("return new()");
								sb.EnterBlock("new");
								foreach (var member in typeDef.Members)
								{
									sb.AppendLine($"{member.OriginalName} = obj.Get<{sb.TypeName(member.Type)}>({sb.Constant(member.Name)}, default),");
								}
								sb.LeaveBlock("new", semicolon: true);
							}
							else
							{
								// var obj = value.AsObject();
								// var instance = Activator.CreateInstance<TValue>();
								// foreach(var kv in obj)
								// {
								//     switch (kv.Key)
								//     {
								//         case "foo": FooAccessor(instance) = kv.Value.As<TValue.Foo>(resolver); break;
								//         case "bar": BarAccessor(instnace) = kv.Value.As<TValue.TBar>(resolver); break;
								//         ...
								//     }
								// }
								// return instance;

								sb.AppendLine("var obj = value.AsObject();");
								sb.AppendLine($"var instance = {sb.MethodName(typeof(Activator), nameof(Activator.CreateInstance))}<{typeName}>();");
								sb.NewLine();
								sb.AppendLine("foreach (var kv in obj)");
								sb.EnterBlock("foreach");
								sb.AppendLine("switch (kv.Key)");
								sb.EnterBlock("switch");
								foreach (var member in typeDef.Members)
								{
									var getter = GenerateGetterForMember(sb, member.Type, sb.Constant(member.Type, member.DefaultValue));

									sb.AppendLine($"case {sb.Constant(member.Name)}: {member.OriginalName}Accessor({(isValueType ? "ref " : "")}instance) = {getter}; break;");
								}
								sb.LeaveBlock("switch");
								sb.LeaveBlock("foreach");

								sb.NewLine();
								sb.Return("instance");
							}

						}
					);

					sb.AppendLine("#endregion");
					sb.NewLine();

					#endregion
				}
			);

			sb.AppendLine("#endregion");
			sb.NewLine();

		}

		private string? GenerateGetterForMember(CodeBuilder sb, Type memberType, string defaultValue)
		{
			if (this.TypeMap.ContainsKey(memberType))
			{
				return $"{GetLocalSerializerRef(memberType)}.{nameof(IJsonDeserializer<object>.JsonDeserialize)}(kv.Value, resolver)!";
			}

			if (memberType.IsValueType)
			{
				if (!memberType.IsNullableType())
				{
					if (memberType == typeof(bool)) return $"kv.Value.{nameof(JsonValue.ToBoolean)}()";
					if (memberType == typeof(int)) return $"kv.Value.{nameof(JsonValue.ToInt32)}()";
					if (memberType == typeof(long)) return $"kv.Value.{nameof(JsonValue.ToInt64)}()";
					if (memberType == typeof(float)) return $"kv.Value.{nameof(JsonValue.ToSingle)}()";
					if (memberType == typeof(double)) return $"kv.Value.{nameof(JsonValue.ToDouble)}()";
					if (memberType == typeof(Guid)) return $"kv.Value.{nameof(JsonValue.ToGuid)}()";
					if (memberType == typeof(Uuid128)) return $"kv.Value.{nameof(JsonValue.ToUuid128)}()";
					if (memberType == typeof(Uuid64)) return $"kv.Value.{nameof(JsonValue.ToUuid64)}()";
					if (memberType == typeof(DateTime)) return $"kv.Value.{nameof(JsonValue.ToDateTime)}()";
					if (memberType == typeof(DateTimeOffset)) return $"kv.Value.{nameof(JsonValue.ToDateTimeOffset)}()";
					if (memberType == typeof(NodaTime.Instant)) return $"kv.Value.{nameof(JsonValue.ToInstant)}()";
					if (memberType == typeof(NodaTime.Duration)) return $"kv.Value.{nameof(JsonValue.ToDuration)}()";
					if (memberType == typeof(char)) return $"kv.Value.{nameof(JsonValue.ToChar)}()";
					if (memberType == typeof(uint)) return $"kv.Value.{nameof(JsonValue.ToUInt32)}()";
					if (memberType == typeof(ulong)) return $"kv.Value.{nameof(JsonValue.ToUInt64)}()";
					if (memberType == typeof(byte)) return $"kv.Value.{nameof(JsonValue.ToByte)}()";
					if (memberType == typeof(sbyte)) return $"kv.Value.{nameof(JsonValue.ToSByte)}()";
					if (memberType == typeof(decimal)) return $"kv.Value.{nameof(JsonValue.ToDecimal)}()";
					if (memberType == typeof(Half)) return $"kv.Value.{nameof(JsonValue.ToDecimal)}()";
					if (memberType == typeof(Int128)) return $"kv.Value.{nameof(JsonValue.ToInt128)}()";
					if (memberType == typeof(UInt128)) return $"kv.Value.{nameof(JsonValue.ToUInt128)}()";
				}
				else
				{
					if (memberType == typeof(bool?)) return $"kv.Value.{nameof(JsonValue.ToBooleanOrDefault)}({defaultValue})";
					if (memberType == typeof(int?)) return $"kv.Value.{nameof(JsonValue.ToInt32OrDefault)}({defaultValue})";
					if (memberType == typeof(long?)) return $"kv.Value.{nameof(JsonValue.ToInt64OrDefault)}({defaultValue})";
					if (memberType == typeof(float?)) return $"kv.Value.{nameof(JsonValue.ToSingleOrDefault)}({defaultValue})";
					if (memberType == typeof(double?)) return $"kv.Value.{nameof(JsonValue.ToDoubleOrDefault)}({defaultValue})";
					if (memberType == typeof(Guid?)) return $"kv.Value.{nameof(JsonValue.ToGuidOrDefault)}({defaultValue})";
					if (memberType == typeof(Uuid128?)) return $"kv.Value.{nameof(JsonValue.ToUuid128OrDefault)}({defaultValue})";
					if (memberType == typeof(Uuid64?)) return $"kv.Value.{nameof(JsonValue.ToUuid64OrDefault)}({defaultValue})";
					if (memberType == typeof(DateTime?)) return $"kv.Value.{nameof(JsonValue.ToDateTimeOrDefault)}({defaultValue})";
					if (memberType == typeof(DateTimeOffset?)) return $"kv.Value.{nameof(JsonValue.ToDateTimeOffsetOrDefault)}({defaultValue})";
					if (memberType == typeof(NodaTime.Instant?)) return $"kv.Value.{nameof(JsonValue.ToInstantOrDefault)}({defaultValue})";
					if (memberType == typeof(NodaTime.Duration?)) return $"kv.Value.{nameof(JsonValue.ToDurationOrDefault)}({defaultValue})";
					if (memberType == typeof(char?)) return $"kv.Value.{nameof(JsonValue.ToCharOrDefault)}({defaultValue})";
					if (memberType == typeof(uint?)) return $"kv.Value.{nameof(JsonValue.ToUInt32OrDefault)}({defaultValue})";
					if (memberType == typeof(ulong?)) return $"kv.Value.{nameof(JsonValue.ToUInt64OrDefault)}({defaultValue})";
					if (memberType == typeof(byte?)) return $"kv.Value.{nameof(JsonValue.ToByteOrDefault)}({defaultValue})";
					if (memberType == typeof(sbyte?)) return $"kv.Value.{nameof(JsonValue.ToSByteOrDefault)}({defaultValue})";
					if (memberType == typeof(decimal?)) return $"kv.Value.{nameof(JsonValue.ToDecimalOrDefault)}({defaultValue})";
					if (memberType == typeof(Half?)) return $"kv.Value.{nameof(JsonValue.ToDecimalOrDefault)}({defaultValue})";
					if (memberType == typeof(Int128?)) return $"kv.Value.{nameof(JsonValue.ToInt128OrDefault)}({defaultValue})";
					if (memberType == typeof(UInt128?)) return $"kv.Value.{nameof(JsonValue.ToUInt128OrDefault)}({defaultValue})";
				}
			}
			else
			{
				if (memberType == typeof(string)) return $"kv.Value.{nameof(JsonValue.ToStringOrDefault)}({defaultValue})!";
				if (memberType == typeof(JsonValue)) return "kv.Value";
				if (memberType == typeof(JsonObject)) return $"kv.Value.{nameof(JsonValueExtensions.AsObjectOrDefault)}()!";
				if (memberType == typeof(JsonArray)) return $"kv.Value.{nameof(JsonValueExtensions.AsArrayOrDefault)}()!";

				if (memberType.IsDictionaryType(out var keyType, out var valueType))
				{
					if (keyType == typeof(string))
					{
						return $"{GetLocalSerializerRef(valueType)}.{nameof(JsonSerializerExtensions.JsonDeserializeDictionary)}<{sb.TypeName(valueType)}>(kv.Value, defaultValue: {defaultValue}, keyComparer: null, resolver: resolver)!";

					}
					//TODO??
				}
				else if (memberType.IsEnumerableType(out var elemType))
				{
					if (this.TypeMap.ContainsKey(elemType))
					{
						if (memberType.IsArray)
						{
							return $"{GetLocalSerializerRef(elemType)}.{nameof(JsonSerializerExtensions.JsonDeserializeArray)}<{sb.TypeName(elemType)}>(kv.Value, defaultValue: {defaultValue}, resolver: resolver)!";
						}
						if (memberType.IsGenericInstanceOf(typeof(List<>)))
						{
							return $"{GetLocalSerializerRef(elemType)}.{nameof(JsonSerializerExtensions.JsonDeserializeList)}<{sb.TypeName(elemType)}>(kv.Value, defaultValue: {defaultValue}, resolver: resolver)!";
						}
					}
					else
					{
						if (memberType.IsArray)
						{
							return $"kv.Value.AsArrayOrDefault()?.ToArray<{sb.TypeName(elemType)}>({defaultValue}, resolver)!";
						}
						if (memberType.IsGenericInstanceOf(typeof(List<>)))
						{
							return $"kv.Value.AsArrayOrDefault()?.ToList<{sb.TypeName(elemType)}>({defaultValue}, resolver)!";
						}
					}
				}
			}

			return $"kv.Value.As<{sb.TypeName(memberType)}>(defaultValue: {defaultValue}, resolver: resolver)!";
		}

	}

	internal sealed class CodeBuilder
	{

		public static class Namespaces
		{
			public const string SystemCodeDomCompilerCompiler = "System.CodeDom.Compiler";
			public const string SystemDiagnosticsNamespace = "System.Diagnostics";
		}


		public readonly StringBuilder Output = new();

		public readonly Stack<string> Structure = [ ];

		public int Depth = 0;

		public string ToStringAndClear()
		{
			if (this.Depth != 0) throw new InvalidOperationException("Code seems to be truncated");
			var s = this.Output.ToString();
			this.Output.Clear();
			return s;
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return this.Output.ToString();
		}

		public void AppendLine(string text)
		{
			this.Output.Append('\t', this.Depth).AppendLine(text);
		}

		public void AppendLine(ref DefaultInterpolatedStringHandler text)
		{
			this.Output.Append('\t', this.Depth).AppendLine(string.Create(CultureInfo.InvariantCulture, ref text));
		}

		public void NewLine()
		{
			this.Output.AppendLine();
		}

		public string Constant(string literal) => "\"" + literal.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; //TODO: more!
		public string Constant(bool literal) => literal ? "true" : "false";
		public string Constant(char c) => c == 0 ? "'\0'" : char.IsLetter(c) || char.IsDigit(c) ? "'{c}'" : $"'\\u{(int) c:x04}'";
		public string Constant(int literal) => literal.ToString(CultureInfo.InvariantCulture);
		public string Constant(long literal) => literal.ToString(CultureInfo.InvariantCulture);
		public string Constant(float literal) => float.IsNaN(literal) ? "float.NaN" : literal.ToString("R", CultureInfo.InvariantCulture);
		public string Constant(double literal) => double.IsNaN(literal) ? "double.NaN" : literal.ToString("R", CultureInfo.InvariantCulture);

		public string Constant(Type type, object? boxed)
		{
			return boxed switch
			{
				null => "null",
				string s => Constant(s),
				bool b => Constant(b),
				int i => Constant(i),
				long l => Constant(l),
				float f => Constant(f),
				double d => Constant(d),
				char c => Constant(c),
				IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
				_ => boxed.ToString() ?? "null",
			};
		}

		public string Constant<TEnum>(TEnum literal) where TEnum : Enum
		{
			return TypeName<TEnum>() + "." + literal.ToString("G");
		}

		public string DefaultOf(string type) => $"default({type})";
		public string DefaultOf<T>() => $"default({TypeName<T>()})";

		public string TypeName(Type t) => TypeHelper.GetCompilableTypeName(t, ommitNamespace: false, global: true);

		public string TypeName<T>() => TypeHelper.GetCompilableTypeName(typeof(T), ommitNamespace: false, global: true);

		public string Parameter(string type, string name, bool nullable = false) => !nullable ? $"{type} {name}" : $"{type}? {name} = default";

		public string Parameter<T>(string name, bool nullable = false) => Parameter(TypeName<T>(), name, nullable);

		public string MethodName(Type parent, string name) => TypeName(parent) + "." + name;

		public string MethodName<T>(string name) => TypeName<T>() + "." + name;

		public void EnterBlock(string type)
		{
			this.Output.Append('\t', this.Depth).AppendLine("{");
			this.Structure.Push(type);
			++this.Depth;
		}

		public void LeaveBlock(string type, bool semicolon = false)
		{
			if (!this.Structure.TryPeek(out var expected) || expected != type)
			{
				throw new InvalidOperationException($"Code structure mismatch: cannot leave '{type}' while inside a '{expected}'");
			}

			--this.Depth;
			this.Structure.Pop();
			this.Output.Append('\t', this.Depth).AppendLine(semicolon ? "};" : "}");
		}

		public void Block(string type, Action statement)
		{
			EnterBlock(type);
			statement();
			LeaveBlock(type);
		}

		public void Call(string method, params ReadOnlySpan<string> args)
		{
			AppendLine($"{method}({string.Join(", ", args!)});");
		}

		public void Return()
		{
			AppendLine("return;");
		}

		public void Return(string expr)
		{
			AppendLine($"return {expr};");
		}

		public void WriteNamespace(string name)
		{
			AppendLine($"namespace {name}");
		}

		public void Namespace(string name, Action block)
		{
			WriteNamespace(name);
			EnterBlock("namespace:" + name);
			NewLine();
			block();
			LeaveBlock("namespace:" + name);
		}

		public void WriteUsing(string name, string? alias = null)
		{
			if (alias == null)
			{
				AppendLine($"using {name};");
			}
			else
			{
				AppendLine($"using {alias} = {name};");
			}
		}

		public void Class(string modifiers, string name, ReadOnlySpan<string> implements, ReadOnlySpan<string> where, Action block)
		{
			if (implements.Length > 0)
			{
				AppendLine($"{modifiers} class {name} : {string.Join(", ", implements!)}");
			}
			else
			{
				AppendLine($"{modifiers} class {name}");
			}

			foreach (var w in where)
			{
				AppendLine($"\twhere {w}");
			}

			EnterBlock("class:" + name);
			NewLine();

			block();

			LeaveBlock("class:" + name);
			NewLine();
		}

		public void Method(string modifiers, string returnType, string name, ReadOnlySpan<string> parameters, Action block)
		{
			AppendLine($"{modifiers} {returnType} {name}({string.Join(", ", parameters!)})");
			EnterBlock("method:" + name);
			block();
			LeaveBlock("method:" + name);
			NewLine();
		}

		public void Comment(string comment)
		{
			if (comment.Contains("\r\n"))
			{
				foreach (var line in comment.Split("\r\n"))
				{
					this.Output.Append('\t', this.Depth).Append("// ").AppendLine(line.TrimEnd());
				}
			}
			else
			{
				this.Output.Append('\t', this.Depth).Append("// ").AppendLine(comment.TrimEnd());
			}
		}

		public void If(string conditionText, Action thenBlock, Action? elseBock = null)
		{
			AppendLine($"if ({conditionText})");

			EnterBlock("then:" + conditionText);
			thenBlock();
			LeaveBlock("then:" + conditionText);

			if (elseBock != null)
			{
				EnterBlock("else:" + conditionText);
				elseBock();
				LeaveBlock("else:" + conditionText);
			}
		}

		public void Attribute(string name, ReadOnlySpan<string> args = default, ReadOnlySpan<(string Name, string Value)> extras = default)
		{
			if (extras.Length == 0)
			{
				AppendLine($"[{name}({string.Join(", ", args!)})]");
			}
			else
			{
				var sb = new StringBuilder();
				sb.Append('[').Append(name).Append('(');
				for (int i = 0; i < args.Length; i++)
				{
					if (i != 0) sb.Append(", ");
					sb.Append(args[i]);
				}

				foreach (var kv in extras)
				{
					sb.Append($", {kv.Name} = {kv.Value}");
				}
				sb.Append(")]");
				AppendLine(sb.ToString());
			}
		}

		public void Attribute<TAttribute>(ReadOnlySpan<string> args = default, ReadOnlySpan<(string Name, string Value)> extras = default)
			where TAttribute : Attribute
		{
			var name = TypeName<TAttribute>();
			if (name.EndsWith("Attribute")) name = name[..^"Attribute".Length];
			Attribute(name, args, extras);
		}

	}

}

#endif
