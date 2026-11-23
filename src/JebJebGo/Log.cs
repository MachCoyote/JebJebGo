using UnityEngine;

namespace JebJebGo
{
    internal static class Log
    {
        private const string Tag = "[JebJebGo] ";

        public static void Info(string message)
        {
            Debug.Log(Tag + message);
        }

        public static void Warning(string message)
        {
            Debug.LogWarning(Tag + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Tag + message);
        }
    }
}
