using System.Collections.Generic;
using System.Threading.Tasks;

namespace romangfit.Services;

public interface IIndexedDbService
{
    Task<List<T>> GetAllAsync<T>(string storeName);
    Task PutAsync<T>(string storeName, T item);
    Task PutBatchAsync<T>(string storeName, List<T> items);
    Task DeleteAsync(string storeName, string id);
    Task ClearAsync(string storeName);
}
