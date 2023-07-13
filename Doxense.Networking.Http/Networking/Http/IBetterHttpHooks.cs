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

namespace Doxense.Networking.Http
{
	using System;
	using System.Net.Sockets;
	using System.Runtime.ExceptionServices;

	public interface IBetterHttpHooks
	{

		void OnStageChanged(BetterHttpClientContext context, BetterHttpClientStage stage);

		void OnError(BetterHttpClientContext context, Exception error);

		bool OnFilterError(BetterHttpClientContext context, Exception error);

		void OnConfigured(BetterHttpClientContext context);

		void OnRequestPrepared(BetterHttpClientContext context);

		void OnRequestCompleted(BetterHttpClientContext context);

		void OnPrepareResponse(BetterHttpClientContext context);

		void OnCompleteResponse(BetterHttpClientContext context);

		void OnFinalizeQuery(BetterHttpClientContext context);

		void OnSocketConnected(BetterHttpClientContext context, Socket socket);

		void OnSocketFailed(BetterHttpClientContext context, Socket socket, Exception error);

	}

	public enum BetterHttpClientStage
	{
		Completed = -1,
		Prepare = 0,
		Configure,
		Send,
		Connecting,
		PrepareRequest,
		CompleteRequest,
		PrepareResponse,
		HandleResponse,
		CompleteResponse,
		Finalize,
	}

}