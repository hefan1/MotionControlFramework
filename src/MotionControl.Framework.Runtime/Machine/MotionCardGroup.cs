using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>
    /// 管理一台设备配置的多张运动卡，并提供按逻辑名称查找轴的入口。
    /// Host 负责创建正确厂商的卡实例；设备流程只使用这里返回的逻辑轴。
    /// </summary>
    public sealed class MotionCardGroup
    {
        /// <summary>设备配置的运动卡只读集合。</summary>
        private readonly ReadOnlyCollection<IMotionCard> _cards;

        /// <summary>设备全部控制卡上的逻辑轴只读集合。</summary>
        private readonly ReadOnlyCollection<IAxis> _axes;

        /// <summary>按逻辑轴名称查找轴实例，名称比较不区分大小写。</summary>
        private readonly Dictionary<string, IAxis> _axesByName;

        /// <summary>串行化多卡连接和断开，避免生命周期操作互相交错。</summary>
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);

        /// <summary>检查并保存本设备配置的运动卡及其逻辑轴。</summary>
        public MotionCardGroup(IEnumerable<IMotionCard> cards)
        {
            if (cards == null)
            {
                throw new ArgumentNullException(nameof(cards));
            }

            var cardList = cards.ToList();
            if (cardList.Count == 0)
            {
                throw new ArgumentException("至少需要配置一张运动控制卡。", nameof(cards));
            }

            ValidateCards(cardList);

            _cards = cardList.AsReadOnly();
            _axesByName = new Dictionary<string, IAxis>(StringComparer.OrdinalIgnoreCase);
            var axisList = new List<IAxis>();

            foreach (var card in cardList)
            {
                foreach (var axis in card.Axes)
                {
                    if (axis == null)
                    {
                        throw new ArgumentException(
                            "控制卡 '" + card.ControllerId + "' 的轴列表中包含空对象。",
                            nameof(cards));
                    }

                    if (string.IsNullOrWhiteSpace(axis.Name))
                    {
                        throw new ArgumentException(
                            "控制卡 '" + card.ControllerId + "' 中有轴没有逻辑名称。",
                            nameof(cards));
                    }

                    if (!string.Equals(axis.Name, axis.Name.Trim(), StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "逻辑轴名称不能包含首尾空格：'" + axis.Name + "'。",
                            nameof(cards));
                    }

                    if (_axesByName.ContainsKey(axis.Name))
                    {
                        throw new ArgumentException(
                            "逻辑轴名称重复：'" + axis.Name + "'。请检查设备硬件配置。",
                            nameof(cards));
                    }

                    _axesByName.Add(axis.Name, axis);
                    axisList.Add(axis);
                }
            }

            _axes = axisList.AsReadOnly();
        }

        /// <summary>本机配置的运动卡。顺序与 Host 传入顺序一致。</summary>
        public IReadOnlyCollection<IMotionCard> Cards
        {
            get { return _cards; }
        }

        /// <summary>所有卡上的逻辑轴集合。</summary>
        public IReadOnlyCollection<IAxis> Axes
        {
            get { return _axes; }
        }

        /// <summary>所有卡均已连接时返回 true。</summary>
        public bool IsConnected
        {
            get { return _cards.All(card => card.IsConnected); }
        }

        /// <summary>按逻辑名称查找轴；名称不存在时抛出带可用名称的异常。</summary>
        public IAxis GetAxis(string logicalAxisName)
        {
            if (string.IsNullOrWhiteSpace(logicalAxisName))
            {
                throw new ArgumentException("逻辑轴名称不能为空。", nameof(logicalAxisName));
            }

            IAxis axis;
            if (_axesByName.TryGetValue(logicalAxisName, out axis))
            {
                return axis;
            }

            var availableNames = string.Join(", ", _axes.Select(item => item.Name));
            throw new KeyNotFoundException(
                "找不到逻辑轴 '" + logicalAxisName + "'。已配置的逻辑轴：" + availableNames + "。");
        }

        /// <summary>
        /// 按配置顺序连接各卡。中途失败时，断开本次调用已连接的卡，避免设备处于半连接状态。
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connectedByThisCall = new List<IMotionCard>();
                foreach (var card in _cards)
                {
                    if (card.IsConnected)
                    {
                        continue;
                    }

                    try
                    {
                        await card.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        connectedByThisCall.Add(card);
                    }
                    catch (Exception connectionError)
                    {
                        if (card.IsConnected)
                        {
                            connectedByThisCall.Add(card);
                        }

                        var rollbackErrors = await RollBackConnectionAsync(connectedByThisCall).ConfigureAwait(false);

                        if (rollbackErrors.Count == 0 && connectionError is OperationCanceledException)
                        {
                            throw;
                        }

                        var failures = new List<Exception>
                        {
                            new InvalidOperationException(
                            "连接运动控制卡 '" + card.ControllerId + "' 失败。",
                            connectionError)
                        };
                        failures.AddRange(rollbackErrors);

                        if (failures.Count == 1)
                        {
                            throw failures[0];
                        }

                        throw new AggregateException(
                            "连接运动控制卡失败，且连接回收过程中还发生错误。",
                            failures);
                    }
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        /// <summary>先确认所有已连接轴停稳，再按连接顺序的反序断开各卡。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureAllConnectedAxesStoppedAsync(cancellationToken).ConfigureAwait(false);

                var failures = new List<Exception>();
                var useCancellationToken = true;
                for (var index = _cards.Count - 1; index >= 0; index--)
                {
                    var card = _cards[index];
                    if (!card.IsConnected)
                    {
                        continue;
                    }

                    try
                    {
                        var token = useCancellationToken ? cancellationToken : CancellationToken.None;
                        await card.DisconnectAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception disconnectError)
                    {
                        failures.Add(new InvalidOperationException(
                            "断开运动控制卡 '" + card.ControllerId + "' 失败。",
                            disconnectError));

                        if (disconnectError is OperationCanceledException || cancellationToken.IsCancellationRequested)
                        {
                            useCancellationToken = false;
                        }
                    }
                }

                if (failures.Count > 0)
                {
                    throw new AggregateException("一张或多张运动控制卡断开失败。", failures);
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        /// <summary>断卡前预检所有已连接轴，避免发现运动状态时已经断开部分控制卡。</summary>
        private async Task EnsureAllConnectedAxesStoppedAsync(CancellationToken cancellationToken)
        {
            foreach (var card in _cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!card.IsConnected)
                {
                    continue;
                }

                foreach (IAxis axis in card.Axes)
                {
                    AxisStatus status;
                    try
                    {
                        status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception readError)
                    {
                        throw new InvalidOperationException(
                            "断开运动控制卡 '" + card.ControllerId + "' 前读取逻辑轴 '" + axis.Name
                            + "' 状态失败；为避免轴仍运动时断卡，已取消断开。",
                            readError);
                    }

                    if (status.Moving)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "'（控制卡 '" + card.ControllerId
                            + "'）仍在运动，必须先停止并确认轴停稳后再断开控制卡。");
                    }
                }
            }
        }

        /// <summary>检查控制卡实例、标识和逻辑轴列表是否有效且不重复。</summary>
        private static void ValidateCards(IReadOnlyCollection<IMotionCard> cards)
        {
            var controllerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (card == null)
                {
                    throw new ArgumentException("运动控制卡列表中包含空对象。", nameof(cards));
                }

                if (string.IsNullOrWhiteSpace(card.ControllerId))
                {
                    throw new ArgumentException("运动控制卡没有配置 ControllerId。", nameof(cards));
                }

                if (!string.Equals(card.ControllerId, card.ControllerId.Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "运动控制卡标识不能包含首尾空格：'" + card.ControllerId + "'。",
                        nameof(cards));
                }

                if (!controllerIds.Add(card.ControllerId))
                {
                    throw new ArgumentException(
                        "运动控制卡标识重复：'" + card.ControllerId + "'。请检查设备硬件配置。",
                        nameof(cards));
                }

                if (card.Axes == null)
                {
                    throw new ArgumentException(
                        "运动控制卡 '" + card.ControllerId + "' 没有提供逻辑轴列表。",
                        nameof(cards));
                }
            }
        }

        /// <summary>连接失败时按相反顺序断开本次已经连上的控制卡。</summary>
        private static async Task<IReadOnlyList<Exception>> RollBackConnectionAsync(
            IReadOnlyList<IMotionCard> connectedCards)
        {
            var rollbackErrors = new List<Exception>();
            for (var index = connectedCards.Count - 1; index >= 0; index--)
            {
                var card = connectedCards[index];
                if (!card.IsConnected)
                {
                    continue;
                }

                try
                {
                    await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception disconnectError)
                {
                    rollbackErrors.Add(new InvalidOperationException(
                        "连接失败后的回收操作中，断开运动控制卡 '" + card.ControllerId + "' 也失败。",
                        disconnectError));
                }
            }

            return rollbackErrors.AsReadOnly();
        }
    }
}
