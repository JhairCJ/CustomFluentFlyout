using System.Threading.Channels;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Single consumer; neither device work nor continuations run on the input thread.</summary>
internal sealed class DictationCommandQueue
{
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    public Task Completion { get; }
    public DictationCommandQueue()
    {
        Completion = Task.Run(async () =>
        {
            await foreach (var command in _commands.Reader.ReadAllAsync())
            {
                try { await command().ConfigureAwait(false); }
                catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Error(ex, "Dictation command failed"); }
            }
        });
    }
    public void Enqueue(Func<Task> command) => _commands.Writer.TryWrite(command);
    public void Complete() => _commands.Writer.TryComplete();
}
