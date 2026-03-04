namespace AIROG_GenContext
{
    public interface IContextProvider
    {
        string GetContext(string prompt, int maxTokens);
        int Priority { get; }
        string Name { get; }
        string Description { get; } 
    }
}
