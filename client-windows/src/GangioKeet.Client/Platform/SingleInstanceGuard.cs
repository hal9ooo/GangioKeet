using System.Threading;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Guardia per assicurare una sola istanza dell'applicazione. Usa un Mutex
/// con nome derivato dall'assembly. <see cref="TryAcquire"/> restituisce un
/// oggetto <see cref="IDisposable"/> da tenere per tutta la durata del
/// processo; se restituisce null, un'altra istanza è già in esecuzione.
/// </summary>
public sealed class SingleInstanceGuard
{
    private readonly string _mutexName;

    public SingleInstanceGuard(string appName)
    {
        _mutexName = $"Local\\{appName.Replace(' ', '_')}_SingleInstance";
    }

    /// <summary>
    /// Tenta di acquisire il mutex. Restituisce un <see cref="IDisposable"/>
    /// che rilascia il mutex alla disposizione, o null se un'altra istanza
    /// è già in esecuzione.
    /// </summary>
    public IDisposable? TryAcquire()
    {
        bool createdNew;
        Mutex mutex = new(initiallyOwned: true, _mutexName, out createdNew);

        if (createdNew)
        {
            return new MutexHandle(mutex);
        }

        mutex.Dispose();
        return null;
    }

    private sealed class MutexHandle : IDisposable
    {
        private readonly Mutex _mutex;

        public MutexHandle(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
