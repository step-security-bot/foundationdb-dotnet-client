﻿#region BSD Licence
/* Copyright (c) 2013, Doxense SARL
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

namespace FoundationDB.Client
{
	using FoundationDB.Async;
	using FoundationDB.Client.Utils;
	using FoundationDB.Layers.Tuples;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>Factory class for keys</summary>
	public struct FdbWatch : IDisposable
	{

		private readonly FdbFuture<Slice> m_future;
		private readonly Slice m_key;

		internal FdbWatch(FdbFuture<Slice> future, Slice key)
		{
			Contract.Requires(future != null);
			m_future = future;
			m_key = key;
		}

		public Slice Key { get { return m_key; } }

		public bool IsAlive
		{
			get { return m_future != null && !m_future.Task.IsCompleted; }
		}

		public bool HasChanged
		{
			get { return m_future != null && m_future.Task.Status == TaskStatus.RanToCompletion; }
		}

		public Task<Slice> Task
		{
			get
			{
				return m_future != null ? m_future.Task : null;
			}
		}

		public TaskAwaiter<Slice> GetAwaiter()
		{
			if (m_future != null)
			{
				if (m_future.HasFlag(FdbFuture.Flags.DISPOSED))
				{
					throw new ObjectDisposedException("Cannot await a watch that has already been disposed");
				}
				return m_future.Task.GetAwaiter();
			}
			throw new InvalidOperationException("Cannot await an empty watch");
		}

		public void Cancel()
		{
			if (m_future != null)
			{
				m_future.Cancel();
			}
		}

		public void Dispose()
		{
			if (m_future != null)
			{
				m_future.Dispose();
			}
		}

	}

}
