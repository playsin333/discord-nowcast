// Only for checking the build without Windows (mono's mcs): stand-ins for the
// two Windows Runtime extension classes the program calls by reflection.
// Not part of the real build: build.ps1 and tests\run.ps1 do not take it.
using System.IO;
using System.Threading.Tasks;

namespace System
{
    public interface IAsyncOperation<T>
    {
    }

    public static class WindowsRuntimeSystemExtensions
    {
        public static Task<T> AsTask<T>(IAsyncOperation<T> operation)
        {
            return null;
        }
    }
}

namespace System.IO
{
    public static class WindowsRuntimeStreamExtensions
    {
        public static Stream AsStreamForRead(object stream)
        {
            return null;
        }
    }
}
