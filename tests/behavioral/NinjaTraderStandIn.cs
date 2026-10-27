// Minimal stand-ins for the NinjaTrader 8 API surface used by the add-on. For compile-checking only.
using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace System.Windows
{
    public class Application { public static Application Current { get; set; } public Dispatcher Dispatcher { get; set; } }
}
namespace System.Windows.Threading
{
    public enum DispatcherPriority { Normal }
    public class DispatcherOperation { }
    public class Dispatcher
    {
        public bool HasShutdownStarted { get; set; }
        public DispatcherOperation BeginInvoke(DispatcherPriority p, Delegate d) { return null; }
        public DispatcherOperation InvokeAsync(Action a) { return null; }
    }
}
namespace NinjaTrader.Core { public static class Globals { public static DateTime Now { get { return DateTime.Now; } } public static string UserDataDir { get; set; } } }
namespace NinjaTrader.Core.FloatingPoint { }
namespace NinjaTrader.Gui { }
namespace NinjaTrader.Gui.Chart { public class Chart { } public class ChartControl { } }
namespace NinjaTrader.Gui.Tools { }
namespace NinjaTrader.Data { }

namespace NinjaTrader.Cbi
{
    public enum MarketPosition { Long, Short, Flat }
    public enum OrderState { Accepted, Cancelled, CancelPending, CancelSubmitted, ChangePending, ChangeSubmitted, Filled, Initialized, PartFilled, Rejected, Submitted, TriggerPending, Unknown, Working }
    public enum OrderAction { Buy, BuyToCover, Sell, SellShort }
    public enum OrderType { Market, Limit, StopMarket, StopLimit, MIT }
    public enum ConnectionStatus { Connected, ConnectionLost, Connecting, Disconnected, Disconnecting }
    public enum ErrorCode { NoError, Other }
    public enum AccountItem { RealizedProfitLoss, UnrealizedProfitLoss, NetLiquidation }
    public enum Currency { UsDollar }

    public class MasterInstrument { public double PointValue; public double TickSize; public string FormatPrice(double p) { return p.ToString(); } }
    public class MarketDataEventArgs : EventArgs { }
    public class MarketData { public event EventHandler<MarketDataEventArgs> Update; }
    public class Instrument
    {
        public string FullName; public MasterInstrument MasterInstrument; public Dispatcher Dispatcher; public MarketData MarketData;
        public static Instrument GetInstrument(string name) { return null; }
    }
    public class Order { public string OrderId; public string Name; public OrderAction OrderAction; public OrderType OrderType; public int Quantity; public OrderState OrderState; public Instrument Instrument; }
    public class Execution
    {
        public Account Account; public Instrument Instrument; public int Quantity; public double Price; public DateTime Time;
        public string ExecutionId; public MarketPosition MarketPosition; public Order Order; public double Commission;
    }
    public class Position { public Instrument Instrument; public MarketPosition MarketPosition; public int Quantity; public double AveragePrice; }
    public class ExecutionEventArgs : EventArgs { public Execution Execution; }
    public class Account
    {
        public string Name;
        public List<Order> Orders = new List<Order>();
        public List<Position> Positions = new List<Position>();
        public List<Execution> Executions = new List<Execution>();
        public List<NinjaTrader.NinjaScript.StrategyBase> Strategies = new List<NinjaTrader.NinjaScript.StrategyBase>();
        public static List<Account> All = new List<Account>();
        public event EventHandler<ExecutionEventArgs> ExecutionUpdate;
        public int FlattenCount; public int CancelCalls; public double NetLiq = double.NaN; public double Unrealized = 0; public Connection Connection;
        public void Flatten(ICollection<Instrument> instruments) { FlattenCount++; }
        public void Cancel(IEnumerable<Order> orders) { CancelCalls++; foreach (Order o in new List<Order>(orders)) o.OrderState = OrderState.Cancelled; }
        public static void FlattenEverything() { }
        public double Get(AccountItem item, Currency currency) { return item == AccountItem.NetLiquidation ? NetLiq : (item == AccountItem.UnrealizedProfitLoss ? Unrealized : 0); }
    }
    public class ConnectionOptions { public string Name; }
    public class ConnectionStatusEventArgs : EventArgs
    {
        public Connection Connection; public ConnectionStatus Status, PreviousStatus, PriceStatus, PreviousPriceStatus; public ErrorCode Error; public string NativeError;
    }
    public class Connection
    {
        public static List<Connection> Connections = new List<Connection>();
        public static event EventHandler<ConnectionStatusEventArgs> ConnectionStatusUpdate;
        public ConnectionStatus Status, PriceStatus; public ConnectionOptions Options;
    }
}
namespace NinjaTrader.NinjaScript
{
    public enum State { SetDefaults, Configure, Active, DataLoaded, Historical, Transition, Realtime, Terminated }
    public class StrategyBase { public string Name; public State State; }
    public class NinjaScriptBase
    {
        public string Name; public string Description; public State State;
        protected virtual void OnStateChange() { }
        protected void Print(string s) { }
    }
    public class AddOnBase : NinjaScriptBase { }
}
