using Ferry.Core.AppLocking;
using Ferry.Core.Files;

namespace Ferry.Core.Tests;

/// <summary>The app's PIN lock: the PIN never saved, wrong tries counted across restarts, the decoy after too many.</summary>
public sealed class AppLockTests : IDisposable
{
    private const string Pin = "4821";
    private const string OtherPin = "0000";

    private readonly string _folder = Directory.CreateTempSubdirectory("ferry_lock_").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private SecretStore Secrets => new(_folder);

    private string SecretFile => Path.Combine(_folder, "app-lock.secret");

    // one iteration: the tests check the lock's rules, not how slow its hash is
    private AppLock Lock() => new(Secrets, iterations: 1);

    private AppLock LockedWith(int decoyAfter)
    {
        var appLock = Lock();
        appLock.TurnOn(Pin, decoyAfter);
        return Lock();
    }

    [Fact]
    public void NewInstallIsOff() => Assert.Equal(LockStatus.Off, Lock().Status);

    [Theory]
    [InlineData("4821", true)]
    [InlineData("482", false)]
    [InlineData("48210", false)]
    [InlineData("48a1", false)]
    [InlineData("٤٨٢١", false)]
    public void PinIsFourAsciiDigits(string text, bool pin) => Assert.Equal(pin, AppLock.IsPin(text));

    [Fact]
    public void RightPinUnlocksAndThePinIsNotSaved()
    {
        var appLock = LockedWith(AppLock.DefaultDecoyAfter);

        Assert.Equal(LockStatus.On, appLock.Status);
        Assert.Equal(PinCheck.Unlocked, appLock.Check(Pin));
        Assert.DoesNotContain(Pin, File.ReadAllText(SecretFile), StringComparison.Ordinal);
    }

    [Fact]
    public void WrongPinsOpenTheDecoyAtTheChosenCount()
    {
        var appLock = LockedWith(3);

        Assert.Equal(PinCheck.Wrong, appLock.Check(OtherPin));
        Assert.Equal(PinCheck.Wrong, appLock.Check("12"));
        Assert.Equal(PinCheck.Decoy, appLock.Check(OtherPin));
        Assert.Equal(PinCheck.Wrong, appLock.Check(OtherPin)); // the count started again
        Assert.Equal(PinCheck.Unlocked, appLock.Check(Pin));
    }

    [Fact]
    public void WrongTriesAreCountedAcrossRestarts()
    {
        Assert.Equal(PinCheck.Wrong, LockedWith(3).Check(OtherPin));
        Assert.Equal(PinCheck.Wrong, Lock().Check(OtherPin));

        Assert.Equal(PinCheck.Decoy, Lock().Check(OtherPin));
    }

    [Fact]
    public void RightPinStartsTheCountAgain()
    {
        var appLock = LockedWith(3);
        appLock.Check(OtherPin);
        appLock.Check(OtherPin);

        Assert.Equal(PinCheck.Unlocked, appLock.Check(Pin));
        Assert.Equal(PinCheck.Wrong, Lock().Check(OtherPin));
        Assert.Equal(PinCheck.Wrong, Lock().Check(OtherPin));
    }

    [Fact]
    public void WithTheDecoyOffWrongPinsStayWrong()
    {
        var appLock = LockedWith(AppLock.DecoyOff);

        for (var i = 0; i < 20; i++)
            Assert.Equal(PinCheck.Wrong, appLock.Check(OtherPin));
        Assert.Equal(PinCheck.Unlocked, appLock.Check(Pin));
    }

    [Fact]
    public void ChangingThePinNeedsTheCurrentOne()
    {
        var appLock = LockedWith(AppLock.DefaultDecoyAfter);

        Assert.False(appLock.ChangePin(OtherPin, "1111"));
        Assert.True(appLock.ChangePin(Pin, "1111"));

        Assert.Equal(PinCheck.Wrong, Lock().Check(Pin));
        Assert.Equal(PinCheck.Unlocked, Lock().Check("1111"));
        Assert.Equal(AppLock.DefaultDecoyAfter, Lock().DecoyAfter);
    }

    [Fact]
    public void TurningOffNeedsThePinAndRemovesTheLock()
    {
        var appLock = LockedWith(AppLock.DefaultDecoyAfter);

        Assert.False(appLock.TurnOff(OtherPin));
        Assert.Equal(LockStatus.On, Lock().Status);
        Assert.True(appLock.TurnOff(Pin));

        Assert.Equal(LockStatus.Off, appLock.Status);
        Assert.Equal(LockStatus.Off, Lock().Status);
        Assert.False(File.Exists(SecretFile));
    }

    [Fact]
    public void DecoyCountIsSavedAndChecked()
    {
        var appLock = LockedWith(AppLock.DefaultDecoyAfter);

        appLock.SetDecoyAfter(10);

        Assert.Equal(10, Lock().DecoyAfter);
        Assert.Throws<ArgumentException>(() => appLock.SetDecoyAfter(4));
        Assert.Throws<ArgumentException>(() => Lock().TurnOn(Pin, 7));
        Assert.Throws<ArgumentException>(() => Lock().TurnOn("12345", AppLock.DefaultDecoyAfter));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"salt": "AA==", "hash": "AA==", "iterations": 1, "decoyAfter": 5, "wrongTries": 0}""")]
    public void LockThatCantBeReadStaysLocked(string content)
    {
        Secrets.Save("app-lock", System.Text.Encoding.UTF8.GetBytes(content));

        var appLock = Lock();

        Assert.Equal(LockStatus.Unreadable, appLock.Status);
        Assert.Throws<InvalidOperationException>(() => appLock.Check(Pin));
        Assert.Throws<InvalidOperationException>(() => appLock.TurnOff(Pin));
    }

    [Fact]
    public void CheckingWhenOffIsAMistake() => Assert.Throws<InvalidOperationException>(() => Lock().Check(Pin));
}
