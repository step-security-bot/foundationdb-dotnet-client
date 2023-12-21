﻿#region Copyright (c) 2023 SnowBank SAS, (c) 2005-2023 Doxense SAS
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

namespace FoundationDB.Client
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Threading;
	using System.Threading.Tasks;
	using Doxense.Diagnostics.Contracts;
	using FoundationDB.Client.Core;
	using FoundationDB.Client.Native;
	using FoundationDB.DependencyInjection;
	using FoundationDB.Filters.Logging;

	/// <summary>FoundationDB database session handle</summary>
	/// <remarks>An instance of this class can be used to create any number of concurrent transactions that will read and/or write to this particular database.</remarks>
	[DebuggerDisplay("Root={Root}")]
	public class FdbDatabase : IFdbDatabase, IFdbDatabaseOptions, IFdbDatabaseProvider
	{
		#region Private Fields...

		/// <summary>Underlying handler for this database (native, dummy, memory, ...)</summary>
		private readonly IFdbDatabaseHandler m_handler;

		/// <summary>If true, the database will only allow read-only transactions.</summary>
		private bool m_readOnly;

		/// <summary>Global cancellation source that is cancelled when the current db instance gets disposed.</summary>
		private readonly CancellationTokenSource m_cts;

		/// <summary>Set to true when the current db instance gets disposed.</summary>
		private volatile bool m_disposed;

		/// <summary>Global counters used to generate the transaction's local id (for debugging purpose)</summary>
		internal static int TransactionIdCounter;

		/// <summary>List of all "pending" transactions created from this database instance (and that have not yet been disposed)</summary>
		/// <remarks>Transactions created via a <see cref="FdbTenant">Tenant</see> are stored in this tenant, no here</remarks>
		private readonly ConcurrentDictionary<int, FdbTransaction> m_transactions = new ConcurrentDictionary<int, FdbTransaction>();

		/// <summary>List of all opened tenants created from this database instance (and that have not yet been disposed)</summary>
		private readonly ConcurrentDictionary<FdbTenantName, FdbTenant> m_tenants = new ConcurrentDictionary<FdbTenantName, FdbTenant>(FdbTenantName.Comparer.Default);

		/// <summary>Directory instance corresponding to the root of this database</summary>
		private FdbDirectoryLayer m_directory;

		/// <summary>The root location of this database</summary>
		private FdbDirectorySubspaceLocation m_root;

		/// <summary>Default Timeout value for all transactions</summary>
		private int m_defaultTimeout;

		/// <summary>Default Retry Limit value for all transactions</summary>
		private int m_defaultRetryLimit;

		/// <summary>Default Max Retry Delay value for all transactions</summary>
		private int m_defaultMaxRetryDelay;

		#endregion

		#region Constructors...

		/// <summary>Create a new database instance</summary>
		/// <param name="handler">Handle to the native FDB_DATABASE*</param>
		/// <param name="directory">Directory Layer attached to this instance</param>
		/// <param name="root">Root location of this database</param>
		/// <param name="readOnly">If true, the database instance will only allow read-only transactions</param>
		protected FdbDatabase(IFdbDatabaseHandler handler, FdbDirectoryLayer directory, FdbDirectorySubspaceLocation root, bool readOnly)
		{
			Contract.Debug.Requires(handler != null && directory != null && root != null);

			m_handler = handler;
			m_readOnly = readOnly;
			m_root = root;
			m_directory = directory;
			var cts = new CancellationTokenSource();
			m_cts = cts;
			this.Cancellation = cts.Token;
		}

		/// <summary>Create a new Database instance from a database handler</summary>
		/// <param name="handler">Handle to the native FDB_DATABASE*</param>
		/// <param name="directory">Directory Layer instance used by this database instance</param>
		/// <param name="root">Root location of the database</param>
		/// <param name="readOnly">If true, the database instance will only allow read-only transactions</param>
		public static FdbDatabase Create(IFdbDatabaseHandler handler, FdbDirectoryLayer directory, FdbDirectorySubspaceLocation root, bool readOnly)
		{
			Contract.NotNull(handler);
			Contract.NotNull(directory);
			Contract.NotNull(root);

			return new FdbDatabase(handler, directory, root, readOnly);
		}

		#endregion

		#region Public Properties...

		string IFdbDatabase.Name => "DB";

		public string? ClusterFile => m_handler.ClusterFile;

		/// <summary>Returns a cancellation token that is linked with the lifetime of this database instance</summary>
		/// <remarks>The token will be cancelled if the database instance is disposed</remarks>
		public CancellationToken Cancellation { get; }

		/// <summary>If true, this database instance will only allow starting read-only transactions.</summary>
		public bool IsReadOnly => m_readOnly;

		/// <summary>Root directory of this database instance</summary>
		/// <remarks>Starts at the same path as the <see cref="Root"/> location, meaning that <code>db.Directory["Foo"]</code> will point to the same location as db.Root.Path.Add("Foo").</remarks>
		public IFdbDirectory Directory => m_root;

		/// <summary>Return the DirectoryLayer instance used by this database</summary>
		public FdbDirectoryLayer DirectoryLayer => m_directory;

		/// <summary>Internal handler</summary>
		internal IFdbDatabaseHandler Handler => m_handler;

		/// <summary>Return the currently enforced API version for this database instance.</summary>
		public int GetApiVersion() => m_handler.GetApiVersion();

		/// <summary>Returns a value between 0 and 1 that reflect the saturation of the client main thread.</summary>
		/// <returns>Value between 0 (no activity) and 1 (completly saturated)</returns>
		/// <remarks>The value is updated in the background at regular interval (by default every second).</remarks>
		public double GetMainThreadBusyness() => m_handler.GetMainThreadBusyness();

		#endregion

		#region Transaction Management...

		/// <summary>Start a new transaction on this database</summary>
		/// <param name="mode">Mode of the new transaction (read-only, read-write, ...)</param>
		/// <param name="ct">Optional cancellation token that can abort all pending async operations started by this transaction.</param>
		/// <param name="context">If not null, attach the new transaction to an existing context.</param>
		/// <returns>New transaction instance that can read from or write to the database.</returns>
		/// <remarks>You MUST call Dispose() on the transaction when you are done with it. You SHOULD wrap it in a 'using' statement to ensure that it is disposed in all cases.</remarks>
		/// <example>
		/// using(var tr = db.BeginTransaction(CancellationToken.None))
		/// {
		///		tr.Set(Slice.FromString("Hello"), Slice.FromString("World"));
		///		tr.Clear(Slice.FromString("OldValue"));
		///		await tr.CommitAsync();
		/// }</example>
		[Obsolete("Use BeginTransaction() instead")]
		public ValueTask<IFdbTransaction> BeginTransactionAsync(FdbTransactionMode mode, CancellationToken ct, FdbOperationContext? context = null)
		{
			if (ct.IsCancellationRequested)
			{
				return ValueTask.FromCanceled<IFdbTransaction>(ct);
			}

			try
			{
				return new ValueTask<IFdbTransaction>(BeginTransaction(mode, ct, context));
			}
			catch (Exception e)
			{
				return ValueTask.FromException<IFdbTransaction>(e);
			}
		}

		/// <summary>Start a new transaction on this database</summary>
		/// <param name="mode">Mode of the new transaction (read-only, read-write, ...)</param>
		/// <param name="ct">Optional cancellation token that can abort all pending async operations started by this transaction.</param>
		/// <param name="context">If not null, attach the new transaction to an existing context.</param>
		/// <returns>New transaction instance that can read from or write to the database.</returns>
		/// <remarks>You MUST call Dispose() on the transaction when you are done with it. You SHOULD wrap it in a 'using' statement to ensure that it is disposed in all cases.</remarks>
		public IFdbTransaction BeginTransaction(FdbTransactionMode mode, CancellationToken ct, FdbOperationContext? context = null)
		{
			ct.ThrowIfCancellationRequested();
			if (context == null)
			{
				context = new FdbOperationContext(this, null, mode, ct);
			}
			else
			{
				if (context.Database != this) throw new ArgumentException("This operation context was created for a different database instance", nameof(context));
			}
			return CreateNewTransaction(context);
		}

		internal void ConfigureTransactionDefaults(FdbTransaction trans)
		{
			// set default options..
			if (m_defaultTimeout != 0) trans.Timeout = m_defaultTimeout;
			if (m_defaultRetryLimit != 0) trans.RetryLimit = m_defaultRetryLimit;
			if (m_defaultMaxRetryDelay != 0) trans.MaxRetryDelay = m_defaultMaxRetryDelay;
			if (this.DefaultLogHandler != null)
			{
				trans.SetLogHandler(this.DefaultLogHandler, this.DefaultLogOptions);
			}
		}

		/// <summary>Start a new transaction on this database, with an optional context</summary>
		/// <param name="context">Optional context in which the transaction will run</param>
		internal FdbTransaction CreateNewTransaction(FdbOperationContext context)
		{
			Contract.Debug.Requires(context != null && context.Database != null);
			ThrowIfDisposed();

			// force the transaction to be read-only, if the database itself is read-only
			var mode = context.Mode;
			if (m_readOnly) mode |= FdbTransactionMode.ReadOnly;

			int id = Interlocked.Increment(ref FdbDatabase.TransactionIdCounter);

			// ensure that if anything happens, either we return a valid Transaction, or we dispose it immediately
			FdbTransaction? trans = null;
			try
			{
				var transactionHandler = m_handler.CreateTransaction(context);

				trans = new FdbTransaction(this, null, context, id, transactionHandler, mode);
				RegisterTransaction(trans);
				context.AttachTransaction(trans);
				ConfigureTransactionDefaults(trans);

				// flag as ready
				trans.State = FdbTransaction.STATE_READY;
				return trans;
			}
			catch (Exception)
			{
				if (trans != null)
				{
					context.ReleaseTransaction(trans);
					trans.Dispose();
				}
				throw;
			}
		}

		internal void EnsureTransactionIsValid(FdbTransaction transaction)
		{
			Contract.Debug.Requires(transaction != null);
			if (m_disposed) ThrowIfDisposed();
			//TODO?
		}

		/// <summary>Add a new transaction to the list of tracked transactions</summary>
		internal void RegisterTransaction(FdbTransaction transaction)
		{
			Contract.Debug.Requires(transaction != null);

			if (!m_transactions.TryAdd(transaction.Id, transaction))
			{
				throw Fdb.Errors.FailedToRegisterTransactionOnDatabase(transaction, this);
			}
		}

		/// <summary>Remove a transaction from the list of tracked transactions</summary>
		/// <param name="transaction"></param>
		internal void UnregisterTransaction(FdbTransaction transaction)
		{
			Contract.Debug.Requires(transaction != null);

			//do nothing is already disposed
			if (m_disposed) return;

			// if it belongs to a tenant, forward...
			if (transaction.Tenant != null)
			{
				transaction.Tenant.UnregisterTransaction(transaction);
				return;
			}

			// Unregister the transaction. We do not care if it has already been done
			m_transactions.TryRemove(KeyValuePair.Create(transaction.Id, transaction));
		}

		/// <summary>Add a new transaction to the list of tracked transactions</summary>
		internal void RegisterTenant(FdbTenant tenant)
		{
			Contract.Debug.Requires(tenant != null);

			if (!m_tenants.TryAdd(tenant.Name, tenant))
			{
				throw Fdb.Errors.FailedToRegisterTenantOnDatabase(tenant, this);
			}
		}

		/// <summary>Remove a transaction from the list of tracked transactions</summary>
		/// <param name="tenant"></param>
		internal void UnregisterTenant(FdbTenant tenant)
		{
			Contract.Debug.Requires(tenant != null);

			//do nothing is already disposed
			if (m_disposed) return;

			// Unregister the transaction. We do not care if it has already been done
			m_tenants.TryRemove(KeyValuePair.Create(tenant.Name, tenant));
		}

		public IFdbTenant GetTenant(FdbTenantName name)
		{
			ThrowIfDisposed();

			return m_tenants.TryGetValue(name, out var tenant) ? tenant : OpenTenant(name);
		}

		internal FdbTenant OpenTenant(FdbTenantName name)
		{
			ThrowIfDisposed();

			var nameCopy = name.Copy();

			FdbTenant? tenant = null;
			try
			{
				var handler = m_handler.OpenTenant(nameCopy);
				tenant = new FdbTenant(this, handler, nameCopy);

				var actual = m_tenants.GetOrAdd(nameCopy, tenant); //HACKHACK ! :(
				if (!object.ReferenceEquals(actual, tenant))
				{
					tenant.Dispose();
					tenant = null;
				}

				return actual;
			}
			catch (Exception)
			{
				if (tenant != null)
				{
					m_tenants.TryRemove(new KeyValuePair<FdbTenantName, FdbTenant>(nameCopy, tenant)); //HACKHACK ! :(
					tenant.Dispose();
				}
				throw;
			}
		}

		public void SetDefaultLogHandler(Action<FdbTransactionLog>? handler, FdbLoggingOptions options = default)
		{
			this.DefaultLogHandler = handler;
			this.DefaultLogOptions = options;
		}

		private Action<FdbTransactionLog>? DefaultLogHandler { get; set; }

		private FdbLoggingOptions DefaultLogOptions { get; set; }

		#endregion

		#region Transactionals...

		//NOTE: other bindings use different names or concept for transactionals, and some also support ReadOnly vs ReadWrite transaction
		// - Python uses the @transactional decorator with first arg called db_or_trans
		// - JAVA uses db.run() and db.runAsync(), but does not have a method for read-only transactions
		// - Ruby uses db.transact do |tr|
		// - Go uses db.Transact(...) and db.ReadTransact(...)
		// - NodeJS uses fdb.doTransaction(function(...) { ... })

		// Conventions:
		// - ReadAsync() => read-only transactions, return something to the caller
		// - WriteAsync() => writable transactions, does not return anything to the caller
		// - ReadWriteAsync() => writable transactions, return something to the caller

		#region IFdbReadOnlyRetryable...

		/// <summary>Empty type that is used to prevent ambiguity when switching on delegate types</summary>
		private struct Nothing { }

		private Task<TResult> ExecuteReadOnlyAsync<TState, TIntermediate, TResult>(TState state, Delegate handler, Delegate? success, CancellationToken ct)
		{
			Contract.NotNull(handler);
			if (ct.IsCancellationRequested) return Task.FromCanceled<TResult>(ct);
			ThrowIfDisposed();

			var context = new FdbOperationContext(this, null, FdbTransactionMode.ReadOnly | FdbTransactionMode.InsideRetryLoop, ct);
			return FdbOperationContext.ExecuteInternal<TState, TIntermediate, TResult>(context, state, handler, success);
		}

		/// <inheritdoc/>
		public Task ReadAsync(Func<IFdbReadOnlyTransaction, Task> handler, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<Nothing, Nothing, Nothing>(default(Nothing), handler, null, ct);
		}

		/// <inheritdoc/>
		public Task ReadAsync<TState>(TState state, Func<IFdbReadOnlyTransaction, TState, Task> handler, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<TState, Nothing, Nothing>(state, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TResult>(Func<IFdbReadOnlyTransaction, Task<TResult>> handler, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<Nothing, TResult, TResult>(default(Nothing), handler, null, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TState, TResult>(TState state, Func<IFdbReadOnlyTransaction, TState, Task<TResult>> handler, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<TState, TResult, TResult>(state, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TResult>(Func<IFdbReadOnlyTransaction, Task<TResult>> handler, Action<IFdbReadOnlyTransaction, TResult> success, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<Nothing, TResult, TResult>(default(Nothing), handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TIntermediate, TResult>(Func<IFdbReadOnlyTransaction, Task<TIntermediate>> handler, Func<IFdbReadOnlyTransaction, TIntermediate, TResult> success, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<Nothing, TIntermediate, TResult>(default(Nothing), handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TIntermediate, TResult>(Func<IFdbReadOnlyTransaction, Task<TIntermediate>> handler, Func<IFdbReadOnlyTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<Nothing, TIntermediate, TResult>(default(Nothing), handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadAsync<TState, TIntermediate, TResult>(TState state, Func<IFdbReadOnlyTransaction, TState, Task<TIntermediate>> handler, Func<IFdbReadOnlyTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return ExecuteReadOnlyAsync<TState, TIntermediate, TResult>(state, handler, success, ct);
		}

		#endregion

		#region IFdbRetryable...

		private Task<TResult> ExecuteReadWriteAsync<TState, TIntermediate, TResult>(TState state, Delegate handler, Delegate? success, CancellationToken ct)
		{
			Contract.NotNull(handler);
			if (ct.IsCancellationRequested) return Task.FromCanceled<TResult>(ct);
			ThrowIfDisposed();
			if (m_readOnly) throw new InvalidOperationException("Cannot mutate a read-only database.");

			var context = new FdbOperationContext(this, null, FdbTransactionMode.Default | FdbTransactionMode.InsideRetryLoop, ct);
			return FdbOperationContext.ExecuteInternal<TState, TIntermediate, TResult>(context, state, handler, success);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Action<IFdbTransaction> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync<TState>(TState state, Action<IFdbTransaction, TState> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<TState, object?, object?>(state, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Func<IFdbTransaction, Task> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync<TState>(TState state, Func<IFdbTransaction, TState, Task> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<TState, object?, object?>(state, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Action<IFdbTransaction> handler, Action<IFdbTransaction> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Action<IFdbTransaction> handler, Func<IFdbTransaction, Task> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TResult>(Action<IFdbTransaction> handler, Func<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Func<IFdbTransaction, Task> handler, Action<IFdbTransaction> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task WriteAsync(Func<IFdbTransaction, Task> handler, Func<IFdbTransaction, Task> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, object?>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TResult>(Func<IFdbTransaction, Task<TResult>> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, TResult, TResult>(null, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TState, TResult>(TState state, Func<IFdbTransaction, TState, Task<TResult>> handler, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<TState, TResult, TResult>(state, handler, null, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TResult>(Func<IFdbTransaction, Task<TResult>> handler, Action<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, TResult, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TResult>(Func<IFdbTransaction, Task> handler, Func<IFdbTransaction, Task<TResult>> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TResult>(Func<IFdbTransaction, Task> handler, Func<IFdbTransaction, TResult> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, object?, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TIntermediate, TResult>(Func<IFdbTransaction, Task<TIntermediate>> handler, Func<IFdbTransaction, TIntermediate, TResult> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, TIntermediate, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TIntermediate, TResult>(Func<IFdbTransaction, Task<TIntermediate>> handler, Func<IFdbTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<object?, TIntermediate, TResult>(null, handler, success, ct);
		}

		/// <inheritdoc/>
		public Task<TResult> ReadWriteAsync<TState, TIntermediate, TResult>(TState state, Func<IFdbTransaction, TState, Task<TIntermediate>> handler, Func<IFdbTransaction, TIntermediate, Task<TResult>> success, CancellationToken ct)
		{
			return ExecuteReadWriteAsync<TState, TIntermediate, TResult>(state, handler, success, ct);
		}

		#endregion

		#endregion

		#region Database Options...

		public IFdbDatabaseOptions Options => this;

		/// <inheritdoc/>
		int IFdbDatabaseOptions.ApiVersion => GetApiVersion();

		/// <inheritdoc />
		public IFdbDatabaseOptions SetOption(FdbDatabaseOption option)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option}");

			m_handler.SetOption(option, default);

			return this;
		}

		/// <inheritdoc />
		public IFdbDatabaseOptions SetOption(FdbDatabaseOption option, ReadOnlySpan<char> value)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option} to '{value.ToString()}'");

			var data = FdbNative.ToNativeString(value, nullTerminated: false);
			m_handler.SetOption(option, data.Span);

			return this;
		}

		/// <inheritdoc />
		public IFdbDatabaseOptions SetOption(FdbDatabaseOption option, ReadOnlySpan<byte> value)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option} to '{Slice.Dump(value)}'");

			m_handler.SetOption(option, value);

			return this;
		}

		/// <inheritdoc />
		public IFdbDatabaseOptions SetOption(FdbDatabaseOption option, long value)
		{
			ThrowIfDisposed();

			if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "SetOption", $"Setting database option {option} to {value}");

			// Spec says: "If the option is documented as taking an Int parameter, value must point to a signed 64-bit integer (little-endian), and value_length must be 8."
			Span<byte> tmp = stackalloc byte[8];
			BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
			m_handler.SetOption(option, tmp);

			return this;
		}

		#endregion

		#region Key Space Management...

		/// <summary>Returns the root path used by this database instance</summary>
		public FdbDirectorySubspaceLocation Root => m_root;

		#endregion

		#region Default Transaction Settings...

		/// <summary>Default Timeout value (in milliseconds) for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultTimeout
		{
			get => m_defaultTimeout;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Timeout value cannot be negative");
				m_defaultTimeout = value;
			}
		}

		/// <summary>Default Retry Limit value for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultRetryLimit
		{
			get => m_defaultRetryLimit;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "RetryLimit value cannot be negative");
				m_defaultRetryLimit = value;
			}
		}

		/// <summary>Default Max Retry Delay value for all transactions created from this database instance.</summary>
		/// <remarks>Only effective for future transactions</remarks>
		public int DefaultMaxRetryDelay
		{
			get => m_defaultMaxRetryDelay;
			set
			{
				if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetryDelay value cannot be negative");
				m_defaultMaxRetryDelay = value;
			}
		}

		#endregion

		#region IDisposable...

		private void ThrowIfDisposed()
		{
			if (m_disposed) throw new ObjectDisposedException(this.GetType().Name);
		}

		/// <summary>Close this database instance, aborting any pending transaction that was created by this instance.</summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>Close this database instance, aborting any pending transaction that was created by this instance.</summary>
		protected virtual void Dispose(bool disposing)
		{
			if (!m_disposed)
			{
				m_disposed = true;
				if (disposing)
				{
					try
					{
						// mark this db as dead, but keep the handle alive until after all the callbacks have fired

						// cancel pending transactions (that don't have a tenant)
						foreach (var trans in m_transactions.Values)
						{
							if (trans.StillAlive)
							{
								trans.Cancel();
							}
						}
						m_transactions.Clear();

						// dispose all tenants (and recursively their own transactions)
						foreach (var tenant in m_tenants.Values)
						{
							tenant.Dispose();
						}
						m_tenants.Clear();

						//note: will block until all the registered callbacks have finished executing
						using (m_cts)
						{
							if (!m_cts.IsCancellationRequested)
							{
								try { m_cts.Cancel(); } catch(ObjectDisposedException) { }
							}
						}
					}
					finally
					{
						if (Logging.On && Logging.IsVerbose) Logging.Verbose(this, "Dispose", $"Disposing database handler");
						try { m_handler.Dispose(); }
						catch (Exception e)
						{
							if (Logging.On) Logging.Exception(this, "Dispose", e);
						}
					}
				}
			}
		}

		#endregion

		#region IFdbDatabaseProvider...

		IFdbDatabaseScopeProvider? IFdbDatabaseScopeProvider.Parent => null;

		ValueTask<IFdbDatabase> IFdbDatabaseScopeProvider.GetDatabase(CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			return new ValueTask<IFdbDatabase>(this);
		}

		public IFdbDatabaseScopeProvider<TState> CreateScope<TState>(Func<IFdbDatabase, CancellationToken, Task<(IFdbDatabase Db, TState State)>> start, CancellationToken lifetime = default)
		{
			Contract.NotNull(start);
			return new FdbDatabaseScopeProvider<TState>(this, start, lifetime);
		}

		bool IFdbDatabaseScopeProvider.IsAvailable => true;

		void IFdbDatabaseProvider.Start()
		{
			//NOP
		}

		void IFdbDatabaseProvider.Stop()
		{
			//NOP
		}

		#endregion

	}

}
