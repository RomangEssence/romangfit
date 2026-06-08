using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace romangfit.Services;

public class IndexedDbService : IIndexedDbService
{
    private readonly IJSRuntime _js;

    public IndexedDbService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<List<T>> GetAllAsync<T>(string storeName)
    {
        try
        {
            var result = await _js.InvokeAsync<List<T>>("indexedDbHelper.getAll", storeName);
            return result ?? new List<T>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IndexedDB GetAllAsync error on store {storeName}: {ex.Message}");
            return new List<T>();
        }
    }

    public async Task PutAsync<T>(string storeName, T item)
    {
        try
        {
            await _js.InvokeVoidAsync("indexedDbHelper.put", storeName, item);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IndexedDB PutAsync error on store {storeName}: {ex.Message}");
        }
    }

    public async Task PutBatchAsync<T>(string storeName, List<T> items)
    {
        if (items == null || items.Count == 0) return;
        try
        {
            await _js.InvokeVoidAsync("indexedDbHelper.putBatch", storeName, items);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IndexedDB PutBatchAsync error on store {storeName}: {ex.Message}");
        }
    }

    public async Task DeleteAsync(string storeName, string id)
    {
        try
        {
            await _js.InvokeVoidAsync("indexedDbHelper.delete", storeName, id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IndexedDB DeleteAsync error on store {storeName} for id {id}: {ex.Message}");
        }
    }

    public async Task ClearAsync(string storeName)
    {
        try
        {
            await _js.InvokeVoidAsync("indexedDbHelper.clear", storeName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IndexedDB ClearAsync error on store {storeName}: {ex.Message}");
        }
    }
}
