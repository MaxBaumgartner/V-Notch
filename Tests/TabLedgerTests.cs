using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class TabLedgerTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        public void Pass(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void FirstSightAndSameValueDoNotMove()
    {
        var clock = new Clock();
        var ledger = new TabLedger(() => true, () => clock.Now);
        Assert.False(ledger.Changed("lamp", true));
        Assert.False(ledger.Changed("lamp", true));
        Assert.True(ledger.Changed("lamp", false));
    }

    [Fact]
    public void ChangeWhileHiddenMovesOnceAfterReturn()
    {
        var clock = new Clock();
        bool shown = true;
        var ledger = new TabLedger(() => shown, () => clock.Now);
        ledger.Changed("temp", 21.5);

        shown = false;
        ledger.Hidden();
        clock.Pass(10);
        Assert.False(ledger.Changed("temp", 22.0)); // hidden: nothing moves, nothing recorded

        shown = true;
        ledger.Shown();
        Assert.True(ledger.Changed("temp", 22.0));  // what the user saw was 21.5
        Assert.False(ledger.Changed("temp", 22.0)); // and only once
    }

    [Fact]
    public void QuickReturnTakesNewValuesSilently()
    {
        var clock = new Clock();
        bool shown = true;
        var ledger = new TabLedger(() => shown, () => clock.Now);
        ledger.Changed("count", "3");

        shown = false;
        ledger.Hidden();
        clock.Pass(1);
        shown = true;
        ledger.Shown();
        Assert.False(ledger.Changed("count", "4"));

        clock.Pass(1); // quiet pass over: live changes move again
        Assert.True(ledger.Changed("count", "5"));
    }

    [Fact]
    public void ForgottenRowsCountAsNewAgain()
    {
        var ledger = new TabLedger(() => true);
        ledger.Changed("row.a", "x");
        ledger.Changed("row.b", "y");
        ledger.ForgetAll("row.");
        Assert.False(ledger.Changed("row.a", "z"));
    }
}
