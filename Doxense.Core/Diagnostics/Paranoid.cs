﻿#region Copyright (c) 2005-2023 Doxense SAS
//
// All rights are reserved. Reproduction or transmission in whole or in part, in
// any form or by any means, electronic, mechanical or otherwise, is prohibited
// without the prior written consent of the copyright owner.
//
#endregion

namespace Doxense.Diagnostics.Contracts
{
	using System;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using JetBrains.Annotations;

	/// <summary>Classe helper présente uniquement en mode Paranoid, pour la vérification de pré-requis, invariants, assertions, ...</summary>
	/// <remarks>Les méthodes de cette classes ne sont compilées que si le flag PARANOID_ANDROID est défini</remarks>
	[DebuggerNonUserCode]
	public static class Paranoid
	{
		// https://www.youtube.com/watch?v=rF8khJ7P4Wg

		/// <summary>Retourne false au runtime, et true en mode parano</summary>
		public static bool IsParanoid
		{
			get
			{
#if PARANOID_ANDROID
				return true;
#else
				return false;
#endif
			}
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une pré-condition est vrai, lors de l'entrée dans une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Requires([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Precondition, null);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une pré-condition est vrai, lors de l'entrée dans une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <param name="userMessage">Message décrivant l'erreur (optionnel)</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Requires([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition, string userMessage)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Precondition, userMessage);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une condition est toujours vrai, dans le body dans une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Assert([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Assert, null);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une condition est toujours vrai, dans le body dans une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <param name="userMessage">Message décrivant l'erreur (optionnel)</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Assert([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition, string userMessage)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Assert, userMessage);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une condition est toujours vrai, lors de la sortie d'une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Ensures([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Postcondition, null);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une condition est toujours vrai, lors de la sortie d'une méthode</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <param name="userMessage">Message décrivant l'erreur (optionnel)</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Ensures([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition, string userMessage)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Postcondition, userMessage);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une condition est toujours vrai pendant toute la vie d'une instance</summary>
		/// <param name="condition">Condition qui ne doit jamais être fausse</param>
		/// <param name="userMessage">Message décrivant l'erreur (optionnel)</param>
		/// <remarks>Ne fait rien si la condition est vrai. Sinon déclenche une ContractException, après avoir essayé de breakpointer le debugger</remarks>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void Invariant([AssertionCondition(AssertionConditionType.IS_TRUE)] bool condition, string userMessage)
		{
#if PARANOID_ANDROID
			if (!condition) throw Contract.RaiseContractFailure(SDC.ContractFailureKind.Invariant, userMessage);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une instance n'est pas null (condition: "value != null")</summary>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void NotNull<TValue>([AssertionCondition(AssertionConditionType.IS_NOT_NULL)] TValue value, [InvokerParameterName] string paramName = null, string message = null)
			where TValue : class
		{
#if PARANOID_ANDROID
			if (value == null) throw Contract.FailArgumentNull(paramName, message);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'une string n'est pas null (condition: "value != null")</summary>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void NotNull([AssertionCondition(AssertionConditionType.IS_NOT_NULL)] string value, [InvokerParameterName] string paramName = null, string message = null)
		{
#if PARANOID_ANDROID
			if (value == null) throw Contract.FailArgumentNull(paramName, message);
#endif
		}

		/// <summary>[PARANOID MODE] Vérifie qu'un buffer n'est pas null (condition: "value != null")</summary>
		[Conditional("PARANOID_ANDROID")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		[AssertionMethod]
		public static void NotNull([AssertionCondition(AssertionConditionType.IS_NOT_NULL)] byte[] value, [InvokerParameterName] string paramName = null, string message = null)
		{
#if PARANOID_ANDROID
			if (value == null) throw Contract.FailArgumentNull(paramName, message);
#endif
		}

	}

}
