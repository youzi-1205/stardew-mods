namespace StardewModAssistant;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1(args));
    }    
}
