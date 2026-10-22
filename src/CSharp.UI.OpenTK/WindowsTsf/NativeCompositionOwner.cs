using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// TSF 原生组合的归属者：把原生 sink 回调（Start/Update/End/EditCompleted）捕获为
/// <see cref="NativeCompositionFact"/> 队列，只在真实安全阶段按 ticket 序应用，
/// 驱动 <see cref="TextInputCompositionSession"/> 的核心 Begin/Update/Commit/Cancel。
/// </summary>
/// <remarks>
/// <para>
/// <b>顺序不变量：</b>每个事实在<b>捕获时</b>取来源 ticket（<see cref="ResolveOperationIdLocked"/>），
/// drain 按该序应用；取消意图同样取 ticket 并按 ticket 分界在事实之间插入执行
/// （<see cref="ExecutePendingCancelsUpTo"/>），保证"取消动作前已确认的结果"仍恰好提交一次、
/// 其后的终结残留按已取消段处理。
/// </para>
/// <para>
/// <b>epoch 归因：</b>事实与意图都绑定捕获时 <see cref="_ownerEpoch"/>；epoch 变化（会话重建）
/// 后，旧 epoch 的事实/意图一律拒绝，绝不投给新客户端、也不复活失效会话。
/// </para>
/// <para>
/// <b>取消流水：</b><see cref="RequestCancel"/> 只登记不可变意图（有界 <see cref="MaxCancelIntents"/>）；
/// 真实执行在安全阶段：原生 <c>TerminateComposition</c>（HRESULT 原样记录、负值绝不报成功）
/// → 同步终结回调 → 核心取消 → 清组合上下文。可重试失败（TF_E_NOLOCK/TF_E_DISCONNECTED）
/// 保留意图重试；不可重试如实计数。
/// </para>
/// <para>
/// <b>失活栅栏：</b><c>DeactivationFence</c> 意图由窗口失活栅栏登记——真实执行后置
/// <see cref="_cancelSuppressionTicket"/>，其后的同 epoch 事实按"已取消段残留"拒绝
/// （不 Begin 重建 context、不 Commit）；新组合 Start 事实放行并在应用成功后清除标记。
/// </para>
/// </remarks>
internal sealed class NativeCompositionOwner : IDisposable
{
	private readonly record struct CancelIntent(long SourceTicket, int OwnerEpoch, bool DeactivationFence);

	internal const int MaxPendingFacts = 64;

	internal const int MaxOperationIdentities = 256;

	internal const int MaxAppliedActionHistory = 64;

	private readonly object _gate = new object();

	private readonly AcpTextStore _store;

	private readonly TextInputCompositionSession _session;

	private readonly int _ownerEpoch;

	private readonly List<NativeCompositionFact> _pending = new List<NativeCompositionFact>();

	private readonly Dictionary<string, long> _operationIds = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly List<NativeCompositionAction> _appliedActions = new List<NativeCompositionAction>();

	private readonly Dictionary<NativeCompositionFact, long> _factOperationIds = new Dictionary<NativeCompositionFact, long>();

	private NativeCommittedBaseline _baseline;

	private NativeCompositionContext? _compositionContext;

	private long _observedNativeGeneration;

	private bool _disposed;

	private bool _needsResync;

	private int _droppedFactCount;

	private int _appliedFactCount;

	private int _rejectedFactCount;

	private readonly List<CancelIntent> _cancelIntents = new List<CancelIntent>();

	internal const int MaxCancelIntents = 4;

	private int _cancelRequestedCount;

	private int _cancelRejectedCount;

	private int _cancelOverflowCount;

	private int _cancelAppliedCount;

	private int _cancelDeferredCount;

	private int _cancelTerminateCount;

	private int _cancelTerminateFailureCount;

	private int _cancelTerminateUnavailableCount;

	private int _lastTerminateHResult;

	private bool _hasTerminateHResult;

	private bool _lastTerminateAttempted;

	private bool _lastTerminateTerminalObserved;

	private bool _terminatingNativeComposition;

	private bool _cancelBaselineSyncPending;

	private long _nextSourceTicket;

	private long _nextCoreActionId;

	private (int Anchor, int Length)? _compositionRange;

	private object? _nativeContext;

	private bool _terminalPending;

	private long _cancelSuppressionTicket = -1L;


	private long _completedOperationWatermark;

	private readonly HashSet<long> _appliedAboveWatermark = new HashSet<long>();

	internal int OwnerEpoch => _ownerEpoch;

	internal long ObservedNativeGeneration
	{
		get
		{
			lock (_gate)
			{
				return _observedNativeGeneration;
			}
		}
	}

	internal TextInputAuthoritativeSnapshot Snapshot => _session.Snapshot;

	internal NativeCommittedBaseline Baseline
	{
		get
		{
			lock (_gate)
			{
				return _baseline;
			}
		}
	}

	internal bool HasActiveComposition
	{
		get
		{
			lock (_gate)
			{
				(int, int)? compositionRange = _compositionRange;
				return compositionRange.HasValue;
			}
		}
	}

	internal (int Anchor, int Length)? CompositionRange
	{
		get
		{
			lock (_gate)
			{
				return _compositionRange;
			}
		}
	}

	internal bool HasTerminalPending
	{
		get
		{
			lock (_gate)
			{
				return _terminalPending;
			}
		}
	}

	internal bool HasPendingSafeWork
	{
		get
		{
			lock (_gate)
			{
				return _pending.Count > 0 || _terminalPending || _cancelIntents.Count > 0 || _cancelBaselineSyncPending;
			}
		}
	}

	internal int PendingCount
	{
		get
		{
			lock (_gate)
			{
				return _pending.Count;
			}
		}
	}

	internal long NextPendingFactTicket
	{
		get
		{
			lock (_gate)
			{
				long result;
				if (_pending.Count != 0)
				{
					result = (_factOperationIds.TryGetValue(_pending[0], out var value) ? value : long.MaxValue);
				}
				else
				{
					result = long.MaxValue;
				}
				return result;
			}
		}
	}

	internal bool NeedsResync
	{
		get
		{
			lock (_gate)
			{
				return _needsResync;
			}
		}
	}

	internal int DroppedFactCount => _droppedFactCount;

	internal int AppliedFactCount => _appliedFactCount;

	internal int RejectedFactCount => _rejectedFactCount;

	internal IReadOnlyList<NativeCompositionAction> AppliedActions
	{
		get
		{
			lock (_gate)
			{
				return _appliedActions.ToArray();
			}
		}
	}

	internal int OperationIdentityCount
	{
		get
		{
			lock (_gate)
			{
				return _operationIds.Count;
			}
		}
	}

	internal int CancelRequestedCount => Volatile.Read(in _cancelRequestedCount);

	internal int CancelRejectedCount => Volatile.Read(in _cancelRejectedCount);

	internal int CancelOverflowCount => Volatile.Read(in _cancelOverflowCount);

	internal int CancelAppliedCount => Volatile.Read(in _cancelAppliedCount);

	internal int CancelDeferredCount => Volatile.Read(in _cancelDeferredCount);

	internal int CancelTerminateCount => Volatile.Read(in _cancelTerminateCount);

	internal int CancelTerminateFailureCount => Volatile.Read(in _cancelTerminateFailureCount);

	internal int CancelTerminateUnavailableCount => Volatile.Read(in _cancelTerminateUnavailableCount);

	internal int PendingCancelCount
	{
		get
		{
			lock (_gate)
			{
				return _cancelIntents.Count;
			}
		}
	}

	internal int LastTerminateHResult => Volatile.Read(in _lastTerminateHResult);

	internal bool HasTerminateHResult => Volatile.Read(in _hasTerminateHResult);

	internal bool LastTerminateAttempted => Volatile.Read(in _lastTerminateAttempted);

	internal bool LastTerminateTerminalObserved => Volatile.Read(in _lastTerminateTerminalObserved);

	/// <summary>
	/// 失活段只读状态捕获：capture 时刻自有窗口是否已离开前台焦点。
	/// <c>true</c>=已离开（物理失活段事实）、<c>false</c>=仍在（正常/独立结果）、
	/// <c>null</c>=无探针（回退 ticket 分界）。由宿主会话注入只读 GetFocus/前台判定；
	/// 测试可注入确定性语义覆盖 hidden-HWND 场景。
	/// </summary>
	internal Func<bool?>? CaptureDeactivationProbe { get; set; }

	internal long AllocatedTicketCount
	{
		get
		{
			lock (_gate)
			{
				return _nextSourceTicket;
			}
		}
	}

	internal void BindNativeContext(object? context)
	{
		lock (_gate)
		{
			_nativeContext = context;
		}
	}

	internal NativeCompositionOwner(AcpTextStore store, TextInputCompositionSession session, int ownerEpoch)
	{
		ArgumentNullException.ThrowIfNull(store, "store");
		ArgumentNullException.ThrowIfNull(session, "session");
		_store = store;
		_session = session;
		_ownerEpoch = ownerEpoch;
		_observedNativeGeneration = store.NativeGeneration;
		_baseline = CaptureBaseline();
	}

	private static bool? SafeDeactivationProbe(Func<bool?> probe)
	{
		try
		{
			return probe();
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal void Capture(NativeCompositionFactKind kind, int anchor, int length, string? preedit, bool hasRange, bool selectionChanged = false, bool textChanged = false, string? nativeOperationKey = null, long? sourceTicket = null)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_observedNativeGeneration = _store.NativeGeneration;
			long num = ResolveOperationIdLocked(nativeOperationKey, sourceTicket);
			string? text = preedit;
			if (((text == null) & hasRange) && length > 0)
			{
				string item = _store.ReadAuthoritativeDocument().Text;
				if (anchor >= 0 && anchor + length <= item.Length)
				{
					text = item.Substring(anchor, length);
				}
			}
			string? capturedText = text;
			if (capturedText == null && kind == NativeCompositionFactKind.End)
			{
				capturedText = _store.ReadAuthoritativeDocument().Text;
			}
			bool flag = (uint)kind <= 1u;
			bool? deactivated = CaptureDeactivationProbe is { } fp ? SafeDeactivationProbe(fp) : null;
			NativeCompositionFact nativeCompositionFact = new NativeCompositionFact(kind, _observedNativeGeneration, _ownerEpoch, anchor, length, text, hasRange, selectionChanged, textChanged, hasResidualPreedit: false, NativeCompositionFactSource.Com)
			{
				CapturedNativeText = capturedText,
				CapturedActiveRange = flag ? (hasRange ? new (int, int)?((anchor, length)) : _compositionRange) : _compositionRange,
				CapturedTerminal = kind == NativeCompositionFactKind.End || _terminalPending,
				CapturedDeactivated = deactivated
			};
			EnqueueLocked(nativeCompositionFact, num);
			switch (kind)
			{
			case NativeCompositionFactKind.Start:
			case NativeCompositionFactKind.Update:
				if (hasRange)
				{
					_compositionRange = (anchor, length);
				}
				_terminalPending = false;
				break;
			case NativeCompositionFactKind.End:
				_compositionRange = null;
				_terminalPending = true;
				if (Volatile.Read(in _terminatingNativeComposition))
				{
					Volatile.Write(ref _lastTerminateTerminalObserved, value: true);
				}
				break;
			case NativeCompositionFactKind.Cancel:
				_compositionRange = null;
				_terminalPending = false;
				break;
			case NativeCompositionFactKind.EditCompleted:
				break;
			}
		}
	}

	internal void CaptureCancel(string? nativeOperationKey = null, long? sourceTicket = null)
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				long operationId = ResolveOperationIdLocked(nativeOperationKey, sourceTicket);
				NativeCompositionFact fact = new NativeCompositionFact(NativeCompositionFactKind.Cancel, _observedNativeGeneration, _ownerEpoch, 0, 0, null, hasRange: false, selectionChanged: false, textChanged: false, hasResidualPreedit: false, NativeCompositionFactSource.Com);
				EnqueueLocked(fact, operationId);
				_compositionRange = null;
				_terminalPending = false;
			}
		}
	}

	/// <summary>
	/// <b>宿主取消入口</b>：登记一次取消意图（取<b>较低</b>的来源 ticket）并尝试在真实安全阶段执行。
	/// </summary>
	/// <returns>取消是否已<b>真实执行</b>（原生终止已尝试 + 核心取消已应用）；延后/拒绝一律 false。</returns>
	/// <remarks>
	/// <para>
	/// <b>为什么不直接取消核心客户端：</b>直接 <c>CancelComposition</c> 只清核心镜像，原生 TSF 组合仍活着，
	/// 之后真实 <c>OnEndComposition</c>/完成编辑会按残留预编辑分类，可能把旧预编辑写回绑定。
	/// 因此必须先由 owner 登记取消（较低 ticket），再在安全阶段
	/// <c>TerminateComposition</c> → 同步终结回调 → 核心取消 → 刷新组合上下文。
	/// </para>
	/// <para>
	/// <b>不满足安全前置时只排队：</b>所属线程、store 安全阶段（不在 <c>RequestLock</c> 栈内/持锁/通知内）、
	/// 未重入、未延后时立即执行；否则保留意图并由下一次真实安全阶段执行（有界，不轮询）。
	/// </para>
	/// <para><b>epoch/Dispose：</b>意图绑定登记时的 epoch；epoch 变化或已释放时既不会投给新 client，也不复活失效会话。</para>
	/// </remarks>
	internal bool RequestCancel(bool executeNow = true, bool deactivationFence = false)
	{
		Interlocked.Increment(ref _cancelRequestedCount);
		lock (_gate)
		{
			if (_disposed || _needsResync)
			{
				Interlocked.Increment(ref _cancelRejectedCount);
				return false;
			}
			if (_cancelIntents.Count >= 4)
			{
				Interlocked.Increment(ref _cancelOverflowCount);
				return false;
			}
			long num = ResolveOperationIdLocked(null);
			_cancelIntents.Add(new CancelIntent(num, _ownerEpoch, deactivationFence));
		}
		if (executeNow)
		{
			return ExecutePendingCancels();
		}
		return false;
	}

	internal bool ExecutePendingCancels()
	{
		return ExecutePendingCancelsUpTo(long.MaxValue);
	}

	/// <summary>
	/// 只执行<b>失活栅栏</b>意图（不受 ticket 分界）：fence 由真实失活确认消息
	/// （KILLFOCUS/WM_ACTIVATE(WA_INACTIVE)）登记——登记即意味着失活已发生，
	/// 其意图 ticket 可能晚于失活批 fact（TIP 经 TSF focus 通道先 capture），
	/// 但归因由 <c>CapturedDeactivated</c> 物理焦点判据承担，不靠 ticket。
	/// </summary>
	internal bool ExecuteDeactivationFences()
	{
		// 执行在锁外——ApplyCancelIntent→CancelCoreComposition 可经核心回调同步重入
		// ExecutePendingCancels*，故批次留在方法栈帧（stackalloc）而非共享实例：
		// 同步重入拿不到本帧 span，且 CancelIntent 为 unmanaged、MaxCancelIntents 有界。
		Span<CancelIntent> batch = stackalloc CancelIntent[MaxCancelIntents];
		int dueCount;
		lock (_gate)
		{
			if (_disposed || _cancelIntents.Count == 0)
			{
				return false;
			}
			if (!_store.IsOnOwnerThread || !_store.IsSafePhase)
			{
				// drain 内机会点：前置不满足即跳过（不计 deferred——每 fact 调用会污染计数）。
				return false;
			}
			var count = _cancelIntents.Count;
			var keep = 0;
			dueCount = 0;
			for (var i = 0; i < count; i++)
			{
				var intent = _cancelIntents[i];
				if (intent.DeactivationFence)
				{
					batch[dueCount++] = intent;
				}
				else
				{
					_cancelIntents[keep++] = intent;
				}
			}
			if (dueCount == 0)
			{
				return false;
			}
			_cancelIntents.RemoveRange(keep, count - keep);
		}
		var applied = false;
		for (var i = 0; i < dueCount; i++)
		{
			if (ApplyCancelIntent(batch[i]))
			{
				applied = true;
			}
		}
		return applied;
	}

	internal bool ExecutePendingCancelsUpTo(long boundaryTicket)
	{
		// 同上：批次驻留调用栈帧，锁内按 ticket 分界原地 compact，锁外安全执行。
		Span<CancelIntent> batch = stackalloc CancelIntent[MaxCancelIntents];
		int dueCount;
		lock (_gate)
		{
			if (_disposed || _cancelIntents.Count == 0)
			{
				return false;
			}
			if (!_store.IsOnOwnerThread || !_store.IsSafePhase)
			{
				Interlocked.Increment(ref _cancelDeferredCount);
				return false;
			}
			var count = _cancelIntents.Count;
			var keep = 0;
			dueCount = 0;
			for (var i = 0; i < count; i++)
			{
				var intent = _cancelIntents[i];
				if (intent.SourceTicket < boundaryTicket)
				{
					batch[dueCount++] = intent;
				}
				else
				{
					_cancelIntents[keep++] = intent;
				}
			}
			if (dueCount == 0)
			{
				return false;
			}
			_cancelIntents.RemoveRange(keep, count - keep);
		}
		var applied = false;
		for (var i = 0; i < dueCount; i++)
		{
			if (ApplyCancelIntent(batch[i]))
			{
				applied = true;
			}
		}
		return applied;
	}

	private bool ApplyCancelIntent(CancelIntent intent)
	{
		if (intent.OwnerEpoch != _ownerEpoch || _disposed)
		{
			Interlocked.Increment(ref _cancelRejectedCount);
			return false;
		}
		bool flag = TryTerminateNativeComposition();
		if (!flag && IsRetryableTerminateFailure())
		{
			Interlocked.Increment(ref _cancelDeferredCount);
			RequeueCancelIntent(intent);
			return false;
		}
		if (!flag)
		{
			Interlocked.Increment(ref _cancelRejectedCount);
			return false;
		}
		CancelCoreComposition();
		lock (_gate)
		{
			_compositionRange = null;
			_terminalPending = false;
			_compositionContext = null;
			_baseline = CaptureBaseline();
		}
		lock (_gate)
		{
			_cancelBaselineSyncPending = true;
			AdvanceWatermarkLocked(intent.SourceTicket);
			if (intent.DeactivationFence)
			{
				_cancelSuppressionTicket = intent.SourceTicket;
			}
		}
		TrySynchronizeCancelledBaseline();
		Interlocked.Increment(ref _cancelAppliedCount);
		return true;
	}

	private void TrySynchronizeCancelledBaseline()
	{
		if (_cancelBaselineSyncPending && SynchronizeCommittedBaseline())
		{
			_cancelBaselineSyncPending = false;
		}
	}

	private bool IsRetryableTerminateFailure()
	{
		if (!Volatile.Read(in _lastTerminateAttempted))
		{
			return false;
		}
		int num = Volatile.Read(in _lastTerminateHResult);
		if (num != -2147220991 && num != -2147220217)
		{
			return num == -2147418113;
		}
		return true;
	}

	private void RequeueCancelIntent(CancelIntent intent)
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				if (_cancelIntents.Count >= 4)
				{
					Interlocked.Increment(ref _cancelOverflowCount);
				}
				else
				{
					_cancelIntents.Insert(0, intent);
				}
			}
		}
	}

	private bool TryTerminateNativeComposition()
	{
		object? nativeContext = _nativeContext;
		if (nativeContext == null)
		{
			Interlocked.Increment(ref _cancelTerminateUnavailableCount);
			Volatile.Write(ref _lastTerminateAttempted, value: false);
			return false;
		}
		Volatile.Write(ref _lastTerminateAttempted, value: true);
		Volatile.Write(ref _lastTerminateTerminalObserved, value: false);
		Volatile.Write(ref _terminatingNativeComposition, value: true);
		int num;
		try
		{
			num = TsfOwnerCompositionServices.TerminateComposition(nativeContext, IntPtr.Zero);
		}
		finally
		{
			Volatile.Write(ref _terminatingNativeComposition, value: false);
		}
		Interlocked.Increment(ref _cancelTerminateCount);
		Volatile.Write(ref _lastTerminateHResult, num);
		Volatile.Write(ref _hasTerminateHResult, value: true);
		Volatile.Write(ref _lastTerminateAttempted, value: true);
		if (num < 0)
		{
			Interlocked.Increment(ref _cancelTerminateFailureCount);
			return false;
		}
		return true;
	}

	private bool CancelCoreComposition()
	{
		TextInputCompositionToken? token = _session.Token;
		if (token == null || !_session.Snapshot.IsComposing)
		{
			return false;
		}
		return !_session.Cancel(token, NextCoreActionId()).IsRejected;
	}

	internal void CaptureCompletedEdit(bool selectionChanged = false, bool textChanged = true, string? nativeOperationKey = null, long? sourceTicket = null)
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				_observedNativeGeneration = _store.NativeGeneration;
				long operationId = ResolveOperationIdLocked(nativeOperationKey, sourceTicket);
				string item = _store.ReadAuthoritativeDocument().Text;
				TextSelection? capturedSelection = (selectionChanged ? new TextSelection?(_store.ReadDirectedSelection()) : ((TextSelection?)null));
				bool? deactivated = CaptureDeactivationProbe is { } fp ? SafeDeactivationProbe(fp) : null;
				NativeCompositionFact fact = new NativeCompositionFact(NativeCompositionFactKind.EditCompleted, _observedNativeGeneration, _ownerEpoch, 0, 0, null, hasRange: false, selectionChanged, textChanged, hasResidualPreedit: false, NativeCompositionFactSource.Com)
				{
					CapturedNativeText = item,
					CapturedSelection = capturedSelection,
					CapturedActiveRange = _compositionRange,
					CapturedTerminal = _terminalPending,
					CapturedDeactivated = deactivated
				};
				EnqueueLocked(fact, operationId);
				if (Volatile.Read(in _terminatingNativeComposition))
				{
					Volatile.Write(ref _lastTerminateTerminalObserved, value: true);
				}
			}
		}
	}

	private void EnqueueLocked(NativeCompositionFact fact, long operationId)
	{
		_factOperationIds[fact] = operationId;
		if (_pending.Count >= 64)
		{
			NativeCompositionFact key = _pending[0];
			_pending.RemoveAt(0);
			_factOperationIds.Remove(key);
			_droppedFactCount++;
			_needsResync = true;
		}
		_pending.Add(fact);
	}

	internal long AllocateOperationTicket()
	{
		lock (_gate)
		{
			return _disposed ? 0 : (++_nextSourceTicket);
		}
	}

	private long NextCoreActionIdLocked()
	{
		return ++_nextCoreActionId;
	}

	private long ResolveOperationIdLocked(string? key, long? sourceTicket = null)
	{
		if (sourceTicket.HasValue)
		{
			long valueOrDefault = sourceTicket.GetValueOrDefault();
			if (valueOrDefault > 0)
			{
				if (valueOrDefault > _nextSourceTicket)
				{
					_nextSourceTicket = valueOrDefault;
				}
				return valueOrDefault;
			}
		}
		if (key == null)
		{
			return ++_nextSourceTicket;
		}
		if (_operationIds.TryGetValue(key, out var value))
		{
			return value;
		}
		if (_operationIds.Count >= 256)
		{
			RetireCompletedKeysLocked();
		}
		long num = ++_nextSourceTicket;
		_operationIds[key] = num;
		return num;
	}

	private void RetireCompletedKeysLocked()
	{
		HashSet<long> hashSet = new HashSet<long>();
		foreach (NativeCompositionFact item in _pending)
		{
			if (_factOperationIds.TryGetValue(item, out var value))
			{
				hashSet.Add(value);
			}
		}
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, long> operationId in _operationIds)
		{
			if (!hashSet.Contains(operationId.Value))
			{
				list.Add(operationId.Key);
			}
		}
		foreach (string item2 in list)
		{
			_operationIds.Remove(item2);
		}
	}

	private bool IsReplay(long operationId)
	{
		if (operationId > 0)
		{
			return operationId <= _completedOperationWatermark;
		}
		return false;
	}

	private void AdvanceWatermarkLocked(long operationId)
	{
		if (operationId > 0 && operationId > _completedOperationWatermark)
		{
			_appliedAboveWatermark.Add(operationId);
			while (_appliedAboveWatermark.Remove(_completedOperationWatermark + 1))
			{
				_completedOperationWatermark++;
			}
			if (_appliedAboveWatermark.Count > 64)
			{
				_needsResync = true;
				_appliedAboveWatermark.Clear();
			}
		}
	}

	private void EnterResyncLocked(long currentGeneration)
	{
		_observedNativeGeneration = currentGeneration;
		_needsResync = true;
		_compositionContext = null;
		_pending.Clear();
		_factOperationIds.Clear();
		_compositionRange = null;
		_terminalPending = false;
		_cancelIntents.Clear();
	}

	internal NativeCompositionDrainResult DrainAtSafePhase()
	{
		NativeCompositionFact[] pendingFacts;
		lock (_gate)
		{
			if (_disposed || _pending.Count == 0)
			{
				return NativeCompositionDrainResult.Empty;
			}
			if (!_store.IsOnOwnerThread || !_store.IsSafePhase)
			{
				return NativeCompositionDrainResult.Deferred;
			}
			if (_needsResync)
			{
				return NativeCompositionDrainResult.Stale;
			}
			pendingFacts = _pending.ToArray();
			_pending.Clear();
		}
		List<TextInputAdapterResult> results = new List<TextInputAdapterResult>();
		List<NativeCompositionAction> appliedActions = new List<NativeCompositionAction>();
		int rejectedCount = 0;
		foreach (NativeCompositionFact fact in pendingFacts)
		{
			long factTicket;
			lock (_gate)
			{
				factTicket = (_factOperationIds.TryGetValue(fact, out var value) ? value : (++_nextSourceTicket));
				_factOperationIds.Remove(fact);
			}
			if (fact.OwnerEpoch != _ownerEpoch)
			{
				rejectedCount++;
				continue;
			}
			bool isReplay;
			lock (_gate)
			{
				isReplay = IsReplay(factTicket);
			}
			if (isReplay)
			{
				rejectedCount++;
				continue;
			}
			// fence 的时序语义：fence 之前已确认的事实（capture 时焦点仍在，deactivated==false，
			// 即"独立结果"）必须在取消前照常应用；失活段事实（capture 时焦点已离开，
			// deactivated==true）到达 drain 时先执行 fence 再抑制——TIP 经 TSF focus 通道可
			// 先于任何窗口消息 capture，故 fence 意图 ticket 不能作为分界。
			if (fact.CapturedDeactivated == true)
			{
				ExecuteDeactivationFences();
			}

			ExecutePendingCancelsUpTo(factTicket);
			// 失活归因判据（fence 应用后才生效，deactivated 是捕获时刻冻结值）：
			// fence 执行后，同 epoch 内 ticket 晚于意图的事实一律拒（含焦点已恢复的
			// deactivated==false——false 只豁免 fence 前已确认的独立结果，不授权 fence
			// 后旧 owner 事实）；deactivated==true（capture 时焦点已离开）无论 ticket
			// 一律拒——TIP 经 TSF focus 通道可先于任何窗口消息 capture，ticket 序无法归因。
			bool suppressed;
			lock (_gate)
			{
				suppressed = _cancelSuppressionTicket >= 0
					&& (fact.CapturedDeactivated == true
						|| factTicket > _cancelSuppressionTicket);
			}

			if (suppressed)
			{
				rejectedCount++;
				lock (_gate)
				{
					AdvanceWatermarkLocked(factTicket);
				}
				continue;
			}
			if (fact.Kind == NativeCompositionFactKind.EditCompleted)
			{
				(IReadOnlyList<TextInputAdapterResult>, IReadOnlyList<NativeCompositionAction>, int, long) edit = CompleteEdit(fact, factTicket);
				results.AddRange(edit.Item1);
				appliedActions.AddRange(edit.Item2);
				rejectedCount += edit.Item3;
				lock (_gate)
				{
					AdvanceWatermarkLocked(edit.Item4);
				}
				continue;
			}
			NativeCompositionAction action = Classify(fact);
			TextInputAdapterResult result = Apply(fact, action);
			results.Add(result);
			appliedActions.Add(action);
			if (result.IsRejected)
			{
				rejectedCount++;
				continue;
			}
			lock (_gate)
			{
				// 成功应用 → 推进单调水位；fence 标记不随 Start 清除（失活栅栏=owner 生命周期边界，
				// 旧段迟到 Start 不得复活写权限；新组合在新 epoch 下由新 owner 自然无 fence）。
				AdvanceWatermarkLocked(factTicket);
			}
		}
		lock (_gate)
		{
			_appliedFactCount += pendingFacts.Length - rejectedCount;
			_rejectedFactCount += rejectedCount;
			_appliedActions.AddRange(appliedActions);
			if (_appliedActions.Count > 64)
			{
				_appliedActions.RemoveRange(0, _appliedActions.Count - 64);
			}
		}
		TrySynchronizeCancelledBaseline();
		return new NativeCompositionDrainResult(results, appliedActions, rejectedCount, _needsResync);
	}

	private static NativeCompositionAction Classify(NativeCompositionFact fact)
	{
		switch (fact.Kind)
		{
		case NativeCompositionFactKind.Start:
		case NativeCompositionFactKind.Update:
			return (!fact.HasRange) ? NativeCompositionAction.Reject : NativeCompositionAction.Preedit;
		case NativeCompositionFactKind.Cancel:
			return NativeCompositionAction.Cancel;
		case NativeCompositionFactKind.End:
			return NativeCompositionAction.Ignore;
		default:
			return NativeCompositionAction.Ignore;
		}
	}

	private TextInputAdapterResult Apply(NativeCompositionFact fact, NativeCompositionAction action)
	{
		TextInputCompositionToken? token = _session.Token;
		return action switch
		{
			NativeCompositionAction.Preedit => ApplyPreedit(fact, token), 
			NativeCompositionAction.Cancel => _session.Cancel(token, NextCoreActionId()), 
			NativeCompositionAction.Reject => TextInputAdapterResult.Rejected(_session.Snapshot, "原生组合范围不可用；不写入（不伪造 caret 位置）。"), 
			_ => TextInputAdapterResult.NoOp(_session.Snapshot, "该原生事实无需处理。"), 
		};
	}

	private TextInputAdapterResult ApplyPreedit(NativeCompositionFact fact, TextInputCompositionToken? token)
	{
		if (!fact.HasRange)
		{
			return TextInputAdapterResult.Rejected(_session.Snapshot, "原生组合范围不可用；不写入（不伪造 caret 位置）。");
		}
		if (token != null && _session.Snapshot.IsComposing)
		{
			TextInputAdapterResult result = _session.UpdatePreedit(token, NextCoreActionId(), fact.Preedit, fact.Preedit.Length);
			if (!result.IsRejected)
			{
				RefreshCompositionContextLocked();
			}
			return result;
		}
		lock (_gate)
		{
			_baseline = CaptureBaseline();
		}
		if (!TryResolveBaselineReplacement(out TextRange replacement, out string? reason))
		{
			return TextInputAdapterResult.Rejected(_session.Snapshot, reason);
		}
		TextInputAdapterResult result2 = _session.Begin(replacement, fact.Preedit, fact.Preedit.Length);
		if (!result2.IsRejected)
		{
			RefreshCompositionContextLocked();
		}
		return result2;
	}

	private bool TryResolveBaselineReplacement(out TextRange replacement, [NotNullWhen(false)] out string? reason)
	{
		replacement = default;
		NativeCommittedBaseline baseline = Baseline;
		TextInputAuthoritativeSnapshot snapshot = _session.Snapshot;
		int committedLength = snapshot.CommittedLength;
		if (!string.Equals(baseline.CommittedText, snapshot.Committed.Text, StringComparison.Ordinal))
		{
			reason = "基线已提交文本与当前权威文档不一致；拒绝（不夹取掩盖）。";
			return false;
		}
		TextRange selectionRange = baseline.SelectionRange;
		if (selectionRange.Start < 0 || selectionRange.End > committedLength)
		{
			reason = $"基线选区 {selectionRange} 超出已提交文档 [0,{committedLength}]；拒绝。";
			return false;
		}
		replacement = selectionRange;
		reason = null;
		return true;
	}

	private (IReadOnlyList<TextInputAdapterResult> Results, IReadOnlyList<NativeCompositionAction> Actions, int RejectedCount, long SourceOperationId) CompleteEdit(NativeCompositionFact fact, long sourceOperationId)
	{
		List<TextInputAdapterResult> list = new List<TextInputAdapterResult>();
		List<NativeCompositionAction> list2 = new List<NativeCompositionAction>();
		int num = 0;
		NativeCompositionContext? compositionContext;
		(int, int)? capturedActiveRange;
		bool capturedTerminal;
		string text;
		lock (_gate)
		{
			if (_needsResync)
			{
				return (Results: list, Actions: list2, RejectedCount: 1, SourceOperationId: sourceOperationId);
			}
			compositionContext = _compositionContext;
			capturedActiveRange = fact.CapturedActiveRange;
			capturedTerminal = fact.CapturedTerminal;
			text = _session.Snapshot.Committed.Text;
			_terminalPending = false;
		}
		string item = _store.ReadAuthoritativeDocument().Text;
		string text2 = fact.CapturedNativeText ?? item;
		TextInputCompositionToken? token = _session.Token;
		string text3 = string.Empty;
		int activeStart = -1;
		int activeEnd = -1;
		if (!capturedTerminal && capturedActiveRange.HasValue)
		{
			(int, int) valueOrDefault = capturedActiveRange.GetValueOrDefault();
			if (valueOrDefault.Item2 > 0 && valueOrDefault.Item1 >= 0 && valueOrDefault.Item1 + valueOrDefault.Item2 <= text2.Length)
			{
				text3 = text2.Substring(valueOrDefault.Item1, valueOrDefault.Item2);
				activeStart = valueOrDefault.Item1;
				activeEnd = valueOrDefault.Item1 + valueOrDefault.Item2;
			}
		}
		if (compositionContext.HasValue)
		{
			NativeCompositionContext context = compositionContext.GetValueOrDefault();
			if (capturedTerminal || capturedActiveRange.HasValue)
			{
				string nativeShape = context.NativeShape;
				bool matchesNativeShape = string.Equals(text2, nativeShape, StringComparison.Ordinal);
				bool matchesCommitted = string.Equals(text2, text, StringComparison.Ordinal);
				if ((capturedTerminal & matchesNativeShape) && context.Preedit.Length > 0)
				{
					TextInputAdapterResult result = _session.CommitResult(token, NextCoreActionId(), context.Preedit, (text3.Length > 0) ? text3 : null, (text3.Length > 0) ? new int?(text3.Length) : ((int?)null));
					list.Add(result);
					list2.Add(NativeCompositionAction.Commit);
					if (result.IsRejected)
					{
						num++;
					}
					RefreshCompositionContextLocked();
				}
				else if (matchesCommitted && !matchesNativeShape)
				{
					if (_session.Snapshot.IsComposing && token != null)
					{
						list.Add(_session.Cancel(token, NextCoreActionId()));
						list2.Add(NativeCompositionAction.Cancel);
					}
					RefreshCompositionContextLocked();
				}
				else
				{
					NativeDocumentDelta delta = ComputeDelta(nativeShape, text2);
					string confirmedOutsideActive = ComputeConfirmedOutsideActive(text2, delta, activeStart, activeEnd);
					if (confirmedOutsideActive.Length > 0)
					{
						TextInputAdapterResult result2 = _session.CommitResult(token, NextCoreActionId(), confirmedOutsideActive, (text3.Length > 0) ? text3 : null, (text3.Length > 0) ? new int?(text3.Length) : ((int?)null));
						list.Add(result2);
						list2.Add(NativeCompositionAction.Commit);
						if (result2.IsRejected)
						{
							num++;
						}
						RefreshCompositionContextLocked();
					}
					else if ((text3.Length == 0) & capturedTerminal)
					{
						TextInputAdapterResult result3 = _session.End(token, NextCoreActionId());
						list.Add(result3);
						list2.Add(NativeCompositionAction.End);
						if (result3.IsRejected)
						{
							num++;
						}
						RefreshCompositionContextLocked();
					}
				}
			}
		}
		else if (!capturedTerminal)
		{
			NativeDocumentDelta delta2 = ComputeDelta(text, text2);
			if (!delta2.IsEmpty)
			{
				TextInputAdapterResult result4 = ApplyDirectNativeWrite(text2, delta2, NextCoreActionId());
				list.Add(result4);
				list2.Add(NativeCompositionAction.Commit);
				if (result4.IsRejected)
				{
					num++;
				}
			}
		}

		TextSelection? capturedSelection = fact.CapturedSelection;
		if (capturedSelection.HasValue)
		{
			TextSelection valueOrDefault3 = capturedSelection.GetValueOrDefault();
			long operationId = NextCoreActionId();
			TextInputCompositionToken? token2 = (_session.Snapshot.IsComposing ? _session.Token : null);
			TextInputAdapterResult item6 = _session.SetNativeSelection(valueOrDefault3, token2, operationId);
			list.Add(item6);
			list2.Add(NativeCompositionAction.Selection);
			if (item6.IsRejected)
			{
				num++;
			}
		}
		lock (_gate)
		{
			_terminalPending = false;
			_observedNativeGeneration = _store.NativeGeneration;
		}
		return (Results: list, Actions: list2, RejectedCount: num, SourceOperationId: sourceOperationId);
	}

	private long NextCoreActionId()
	{
		lock (_gate)
		{
			return NextCoreActionIdLocked();
		}
	}

	private void RefreshCompositionContextLocked()
	{
		lock (_gate)
		{
			TextInputAuthoritativeSnapshot snapshot = _session.Snapshot;
			_compositionContext = (snapshot.IsComposing ? new NativeCompositionContext?(new NativeCompositionContext(snapshot.Committed.Text, snapshot.Replacement, snapshot.Preedit)) : ((NativeCompositionContext?)null));
		}
	}

	private TextInputAdapterResult ApplyDirectNativeWrite(string nativeText, NativeDocumentDelta delta, long commitCoreActionId)
	{
		int committedLength = _session.Snapshot.CommittedLength;
		int num = Math.Clamp(delta.CommonPrefix, 0, committedLength);
		int end = Math.Clamp(num + delta.RemovedLength, num, committedLength);
		TextInputAdapterResult result = _session.Begin(new TextRange(num, end), string.Empty, 0);
		if (result.IsRejected)
		{
			return result;
		}
		return _session.CommitResult(_session.Token, commitCoreActionId, delta.Inserted);
	}

	private static string ComputeConfirmedOutsideActive(string nativeText, NativeDocumentDelta delta, int activeStart, int activeEnd)
	{
		if (delta.Inserted.Length == 0)
		{
			return string.Empty;
		}
		int commonPrefix = delta.CommonPrefix;
		int num = commonPrefix + delta.Inserted.Length;
		if (activeStart < 0 || activeEnd <= activeStart)
		{
			return delta.Inserted;
		}
		int num2 = Math.Max(commonPrefix, activeStart);
		int num3 = Math.Min(num, activeEnd);
		if (num3 <= num2)
		{
			return delta.Inserted;
		}
		string text = ((num2 > commonPrefix) ? nativeText.Substring(commonPrefix, num2 - commonPrefix) : string.Empty);
		string text2 = ((num > num3) ? nativeText.Substring(num3, num - num3) : string.Empty);
		return text + text2;
	}

	internal static NativeDocumentDelta ComputeDelta(string before, string after)
	{
		if (before == null)
		{
			before = string.Empty;
		}
		if (after == null)
		{
			after = string.Empty;
		}
		int i = 0;
		for (int num = Math.Min(before.Length, after.Length); i < num && before[i] == after[i]; i++)
		{
		}
		int j = 0;
		for (int num2 = Math.Min(before.Length - i, after.Length - i); j < num2 && before[before.Length - 1 - j] == after[after.Length - 1 - j]; j++)
		{
		}
		int removedLength = before.Length - i - j;
		int num3 = after.Length - i - j;
		string inserted = ((num3 > 0) ? after.Substring(i, num3) : string.Empty);
		return new NativeDocumentDelta(i, removedLength, inserted);
	}

	private NativeCommittedBaseline CaptureBaseline(string? nativeTextOverride = null)
	{
		TextInputAuthoritativeSnapshot textInputAuthoritativeSnapshot = _session.Refresh();
		string item = _store.ReadAuthoritativeDocument().Text;
		item = nativeTextOverride ?? item;
		TextSelection selection = textInputAuthoritativeSnapshot.Committed.Selection;
		return new NativeCommittedBaseline(textInputAuthoritativeSnapshot.Committed.Text, selection.Anchor, selection.Active, item, _store.NativeGeneration);
	}

	internal bool SynchronizeCommittedBaseline()
	{
		if (!_store.IsOnOwnerThread || !_store.IsSafePhase)
		{
			return false;
		}
		lock (_gate)
		{
			if (_disposed || _needsResync || _pending.Count != 0
				|| _compositionRange.HasValue || _compositionContext.HasValue || _terminalPending)
			{
				return false;
			}
		}
		if (!_session.TryAdoptCommittedBaseline(out TextInputAuthoritativeSnapshot snapshot))
		{
			return false;
		}
		return SynchronizeToNative(snapshot);
	}

	internal bool SynchronizeToNative(TextInputAuthoritativeSnapshot? snapshot = null)
	{
		if (!_store.IsOnOwnerThread || !_store.IsSafePhase)
		{
			return false;
		}
		TextInputAuthoritativeSnapshot snapshot2 = snapshot ?? _session.Snapshot;
		long observedNativeGeneration;
		lock (_gate)
		{
			observedNativeGeneration = _observedNativeGeneration;
		}
		if (!_store.ApplyAuthoritativeUpdate(snapshot2, observedNativeGeneration))
		{
			return false;
		}
		lock (_gate)
		{
			_observedNativeGeneration = _store.NativeGeneration;
			_baseline = CaptureBaseline();
		}
		return true;
	}

	internal void MarkNeedsResync()
	{
		lock (_gate)
		{
			_needsResync = true;
		}
	}

	internal void ClearPending()
	{
		lock (_gate)
		{
			_pending.Clear();
			_factOperationIds.Clear();
			_compositionRange = null;
			_compositionContext = null;
			_terminalPending = false;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				_disposed = true;
				_pending.Clear();
				_factOperationIds.Clear();
				_operationIds.Clear();
				_cancelIntents.Clear();
				_nativeContext = null;
				_appliedActions.Clear();
				_compositionRange = null;
				_compositionContext = null;
				_terminalPending = false;
			}
		}
	}
}


internal readonly record struct NativeCompositionDrainResult(IReadOnlyList<TextInputAdapterResult> Results, IReadOnlyList<NativeCompositionAction> Actions, int RejectedCount, bool NeedsResync)
{
	internal static NativeCompositionDrainResult Empty { get; } = new NativeCompositionDrainResult(Array.Empty<TextInputAdapterResult>(), Array.Empty<NativeCompositionAction>(), 0, NeedsResync: false);

	internal static NativeCompositionDrainResult Deferred { get; } = new NativeCompositionDrainResult(Array.Empty<TextInputAdapterResult>(), Array.Empty<NativeCompositionAction>(), 0, NeedsResync: false);

	internal static NativeCompositionDrainResult Stale { get; } = new NativeCompositionDrainResult(Array.Empty<TextInputAdapterResult>(), Array.Empty<NativeCompositionAction>(), 0, NeedsResync: true);

	internal bool Applied => Results.Count > 0;

	internal bool AnyCommitted
	{
		get
		{
			for (int i = 0; i < Results.Count; i++)
			{
				if (Results[i].Committed)
				{
					return true;
				}
			}
			return false;
		}
	}
}


internal readonly record struct NativeCommittedBaseline(string CommittedText, int SelectionStart, int SelectionEnd, string NativeText, long NativeGeneration)
{
	internal int CommittedLength => CommittedText.Length;

	internal TextRange SelectionRange => new TextRange(Math.Min(SelectionStart, SelectionEnd), Math.Max(SelectionStart, SelectionEnd));

	public override string ToString()
	{
		return $"baseline committedLen={CommittedText.Length} nativeLen={NativeText.Length} sel=[{SelectionRange.Start},{SelectionRange.End}) gen={NativeGeneration}";
	}
}


internal readonly record struct NativeCompositionContext(string CommittedBefore, TextRange Replacement, string Preedit)
{
	internal string NativeShape
	{
		get
		{
			int num = Math.Clamp(Replacement.Start, 0, CommittedBefore.Length);
			int start = Math.Clamp(Replacement.End, num, CommittedBefore.Length);
			return string.Concat(CommittedBefore.AsSpan(0, num), Preedit, CommittedBefore.AsSpan(start));
		}
	}

	public override string ToString()
	{
		return $"ctx committedLen={CommittedBefore.Length} repl=[{Replacement.Start},{Replacement.End}) preeditLen={Preedit.Length}";
	}
}

internal readonly record struct NativeDocumentDelta(int CommonPrefix, int RemovedLength, string Inserted)
{
	internal bool IsEmpty
	{
		get
		{
			if (RemovedLength == 0)
			{
				return Inserted.Length == 0;
			}
			return false;
		}
	}

	public override string ToString()
	{
		return $"delta prefix={CommonPrefix} removed={RemovedLength} insertedLen={Inserted.Length}";
	}
}
