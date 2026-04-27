using KiroCliLib.Core;
using KiroCliLib.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for CallbackHandler.
/// Validates callback registration, invocation, and error isolation.
/// </summary>
public class CallbackHandlerTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CallbackHandler(null!));
    }

    [Fact]
    public void RegisterCallback_NullCallback_ThrowsArgumentNullException()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        Assert.Throws<ArgumentNullException>(() => handler.RegisterCallback(KiroState.Completed, null!));
    }

    [Fact]
    public void Invoke_NullContext_ThrowsArgumentNullException()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        Assert.Throws<ArgumentNullException>(() => handler.Invoke(KiroState.Completed, null!));
    }

    [Fact]
    public void Invoke_RegisteredCallback_IsInvoked()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        CallbackContext? receivedContext = null;

        handler.RegisterCallback(KiroState.Completed, ctx => receivedContext = ctx);

        var context = new CallbackContext { State = KiroState.Completed, Message = "done" };
        handler.Invoke(KiroState.Completed, context);

        Assert.NotNull(receivedContext);
        Assert.Equal(KiroState.Completed, receivedContext!.State);
        Assert.Equal("done", receivedContext.Message);
    }

    [Fact]
    public void Invoke_NoRegisteredCallbacks_DoesNotThrow()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var context = new CallbackContext { State = KiroState.Error };

        // Should not throw
        handler.Invoke(KiroState.Error, context);
    }

    [Fact]
    public void Invoke_DifferentState_DoesNotInvokeCallback()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var invoked = false;

        handler.RegisterCallback(KiroState.Completed, _ => invoked = true);
        handler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error });

        Assert.False(invoked);
    }

    [Fact]
    public void Invoke_MultipleCallbacksSameState_InvokesAll()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var invokeCount = 0;

        handler.RegisterCallback(KiroState.Completed, _ => invokeCount++);
        handler.RegisterCallback(KiroState.Completed, _ => invokeCount++);
        handler.RegisterCallback(KiroState.Completed, _ => invokeCount++);

        handler.Invoke(KiroState.Completed, new CallbackContext { State = KiroState.Completed });

        Assert.Equal(3, invokeCount);
    }

    [Fact]
    public void Invoke_CallbackThrows_OtherCallbacksStillInvoked()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var secondInvoked = false;

        handler.RegisterCallback(KiroState.Completed, _ => throw new InvalidOperationException("boom"));
        handler.RegisterCallback(KiroState.Completed, _ => secondInvoked = true);

        handler.Invoke(KiroState.Completed, new CallbackContext { State = KiroState.Completed });

        Assert.True(secondInvoked, "Second callback should still be invoked after first throws");
    }

    [Fact]
    public void Invoke_CallbackThrows_ErrorIsLogged()
    {
        var handler = new CallbackHandler(_mockLogger.Object);

        handler.RegisterCallback(KiroState.Error, _ => throw new InvalidOperationException("test error"));
        handler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error });

        // Serilog uses generic overload Error<T>(Exception, string, T) for structured logging
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<KiroState>()),
            Times.Once);
    }

    [Fact]
    public void RegisterOnCompleted_RegistersForCompletedState()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var invoked = false;

        handler.RegisterOnCompleted(_ => invoked = true);
        handler.Invoke(KiroState.Completed, new CallbackContext { State = KiroState.Completed });

        Assert.True(invoked);
    }

    [Fact]
    public void Invoke_MultipleStates_OnlyMatchingCallbacksFire()
    {
        var handler = new CallbackHandler(_mockLogger.Object);
        var completedCount = 0;
        var errorCount = 0;

        handler.RegisterCallback(KiroState.Completed, _ => completedCount++);
        handler.RegisterCallback(KiroState.Error, _ => errorCount++);

        handler.Invoke(KiroState.Completed, new CallbackContext { State = KiroState.Completed });
        handler.Invoke(KiroState.Completed, new CallbackContext { State = KiroState.Completed });
        handler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error });

        Assert.Equal(2, completedCount);
        Assert.Equal(1, errorCount);
    }
}
