using System;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Newtonsoft.Json;

namespace TarkFX.StockMarket.Client.Operations
{
    /// <summary>
    /// 股票交易指令。走原版 client/game/profile/items/moving 的异步队列，
    /// 服务端的 TarkFXStockTrade 会扣钱、执行交易，并把 ProfileChanges 带回来。
    /// </summary>
    internal sealed class StockTradeCommand : CommandWithOwners
    {
        [JsonProperty("Action")] public string Action = "TarkFXStockTrade";
        [JsonProperty("stockId")] public string StockId;
        [JsonProperty("shares")] public int Shares;
        [JsonProperty("leverage")] public int Leverage;
        [JsonProperty("op")] public string Op;
    }

    internal sealed class StockTradeOperation : AbstractOperation
    {
        private readonly StockTradeCommand _command;

        public StockTradeOperation(ushort id, ItemController controller, StockTradeCommand command)
            : base(id, controller)
        {
            _command = command;
        }

        public override void ExecuteInternal(Callback callback)
        {
            callback?.Invoke(SuccessfulResult.New);
        }

        public override InventoryOperationDescriptor ToDescriptor()
        {
            return new StockTradeDescriptor
            {
                Operation = this,
                Command = _command
            };
        }

        public override BaseInventoryCommand ToBaseInventoryCommand(string ownerId)
        {
            return _command;
        }

        public override void Dispose()
        {
        }
    }

    internal sealed class StockTradeDescriptor : InventoryOperationDescriptor
    {
        public StockTradeCommand Command;

        public override OperationCreationResult<AbstractOperation> ToInventoryOperation(IPlayer player)
        {
            return new StockTradeOperation(OperationId, player.InventoryController, Command);
        }
    }

    /// <summary>还款指令。同样走 item/moving 队列，扣钱并修改欠债。</summary>
    internal sealed class StockRepayCommand : CommandWithOwners
    {
        [JsonProperty("Action")] public string Action = "TarkFXStockRepay";
        [JsonProperty("currency")] public string Currency;
        [JsonProperty("amount")] public long Amount;
    }

    internal sealed class StockRepayOperation : AbstractOperation
    {
        private readonly StockRepayCommand _command;

        public StockRepayOperation(ushort id, ItemController controller, StockRepayCommand command)
            : base(id, controller)
        {
            _command = command;
        }

        public override void ExecuteInternal(Callback callback)
        {
            callback?.Invoke(SuccessfulResult.New);
        }

        public override InventoryOperationDescriptor ToDescriptor()
        {
            return new StockRepayDescriptor
            {
                Operation = this,
                Command = _command
            };
        }

        public override BaseInventoryCommand ToBaseInventoryCommand(string ownerId)
        {
            return _command;
        }

        public override void Dispose()
        {
        }
    }

    internal sealed class StockRepayDescriptor : InventoryOperationDescriptor
    {
        public StockRepayCommand Command;

        public override OperationCreationResult<AbstractOperation> ToInventoryOperation(IPlayer player)
        {
            return new StockRepayOperation(OperationId, player.InventoryController, Command);
        }
    }
}
