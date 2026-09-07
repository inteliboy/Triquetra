// Simple class to replace value tuples (C# 5 has no ValueTuple/local functions).
namespace Triquetra
{
    sealed class VersionInfo
    {
        public string Short;
        public int[]  Parts;
    }
}
