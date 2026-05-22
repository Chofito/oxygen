namespace Oxygen.Utilities
{
    internal static class OxygenWorker
    {
        [System.ThreadStatic]
        internal static bool Active;
    }
}
