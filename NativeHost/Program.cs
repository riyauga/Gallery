using System.IO.Pipes;
using System.Text;

using var stopped = new CancellationTokenSource();
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var reader = Task.Run(async () =>
{
    var buffer = new byte[1024];
    while (await input.ReadAsync(buffer) > 0) { }
    await stopped.CancelAsync();
});
try
{
    while (!stopped.IsCancellationRequested)
    {
        using var pipe = new NamedPipeClientStream(".", "Gallery.BrowserBridge", PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // 桌面端长时间没开就自己退出：留下的宿主进程会一直把浏览器的 service worker 钉住。
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        attempt.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await pipe.ConnectAsync(attempt.Token);
        }
        catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
        {
            break;
        }
        using var line = new StreamReader(pipe, Encoding.UTF8);
        var port = await line.ReadLineAsync(attempt.Token);
        if (int.TryParse(port, out var number))
        {
            var message = Encoding.UTF8.GetBytes("{\"port\":" + number + "}");
            await output.WriteAsync(BitConverter.GetBytes(message.Length), stopped.Token);
            await output.WriteAsync(message, stopped.Token);
            await output.FlushAsync(stopped.Token);
        }
        await Task.Delay(1000, stopped.Token);
    }
}
catch (OperationCanceledException) { }
