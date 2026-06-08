using Microsoft.JSInterop;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using Microsoft.Extensions.Configuration;
using romangfit.Models;

namespace romangfit.Services;

public class FirebaseService
{
    private readonly IJSRuntime _js;
    private readonly ILocalStorageService _localStorage;
    private readonly IConfiguration _configuration;
    
    public bool IsInitialized { get; private set; }
    
    private const string ConfigStorageKey = "firebase_config";

    public FirebaseService(IJSRuntime js, ILocalStorageService localStorage, IConfiguration configuration)
    {
        _js = js;
        _localStorage = localStorage;
        _configuration = configuration;
    }

    public async Task<bool> CheckAndInitializeAsync()
    {
        if (IsInitialized) return true;
        
        string? savedConfig = null;
        var firebaseSection = _configuration.GetSection("Firebase");
        
        if (firebaseSection.Exists() && !string.IsNullOrEmpty(firebaseSection["ApiKey"]))
        {
            var config = new FirebaseConfig
            {
                ApiKey = firebaseSection["ApiKey"] ?? string.Empty,
                AuthDomain = firebaseSection["AuthDomain"] ?? string.Empty,
                ProjectId = firebaseSection["ProjectId"] ?? string.Empty,
                StorageBucket = firebaseSection["StorageBucket"] ?? string.Empty,
                MessagingSenderId = firebaseSection["MessagingSenderId"] ?? string.Empty,
                AppId = firebaseSection["AppId"] ?? string.Empty
            };
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            savedConfig = JsonSerializer.Serialize(config, options);
            
            // Sync/update local storage to keep it up-to-date
            await _localStorage.SetItemAsync(ConfigStorageKey, savedConfig);
        }
        else
        {
            // Fallback to local storage if appsettings.json does not contain the configuration
            savedConfig = await _localStorage.GetItemAsync<string>(ConfigStorageKey);
        }
        
        if (!string.IsNullOrEmpty(savedConfig))
        {
            IsInitialized = await InitializeAsync(savedConfig);
        }
        return IsInitialized;
    }

    public async Task<bool> InitializeAsync(string configJson)
    {
        try
        {
            var success = await _js.InvokeAsync<bool>("firebaseHelper.initialize", configJson);
            if (success)
            {
                IsInitialized = true;
                await _localStorage.SetItemAsync(ConfigStorageKey, configJson);
            }
            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService Initialize error: {ex.Message}");
            return false;
        }
    }

    public async Task ClearConfigAsync()
    {
        await _localStorage.RemoveItemAsync(ConfigStorageKey);
        IsInitialized = false;
    }

    public async Task<FirebaseUser?> SignUpAsync(string email, string password)
    {
        try
        {
            var result = await _js.InvokeAsync<FirebaseResult>("firebaseHelper.signUp", email, password);
            if (result.Success)
            {
                return new FirebaseUser { Uid = result.Uid ?? "", Email = result.Email ?? "" };
            }
            throw new Exception(result.Error ?? "Sign up failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService SignUp error: {ex.Message}");
            throw;
        }
    }

    public async Task<FirebaseUser?> SignInAsync(string email, string password)
    {
        try
        {
            var result = await _js.InvokeAsync<FirebaseResult>("firebaseHelper.signIn", email, password);
            if (result.Success)
            {
                return new FirebaseUser { Uid = result.Uid ?? "", Email = result.Email ?? "" };
            }
            throw new Exception(result.Error ?? "Sign in failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService SignIn error: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> SignOutAsync()
    {
        try
        {
            var result = await _js.InvokeAsync<bool>("firebaseHelper.signOut");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService SignOut error: {ex.Message}");
            return false;
        }
    }

    public async Task<FirebaseUser?> GetCurrentUserAsync()
    {
        try
        {
            return await _js.InvokeAsync<FirebaseUser?>("firebaseHelper.getCurrentUser");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService GetCurrentUser error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetDocumentAsync<T>(string collectionName, string docId, T data)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var json = JsonSerializer.Serialize(data, options);
            return await _js.InvokeAsync<bool>("firebaseHelper.setDocument", collectionName, docId, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService SetDocument error on {collectionName}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteDocumentAsync(string collectionName, string docId)
    {
        try
        {
            return await _js.InvokeAsync<bool>("firebaseHelper.deleteDocument", collectionName, docId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService DeleteDocument error on {collectionName}: {ex.Message}");
            return false;
        }
    }

    public async Task<List<T>> GetDocumentsAsync<T>(string collectionName)
    {
        try
        {
            var jsonElementList = await _js.InvokeAsync<List<JsonElement>>("firebaseHelper.getDocuments", collectionName);
            var resultList = new List<T>();
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            
            if (jsonElementList != null)
            {
                foreach (var elem in jsonElementList)
                {
                    var item = JsonSerializer.Deserialize<T>(elem.GetRawText(), options);
                    if (item != null)
                    {
                        resultList.Add(item);
                    }
                }
            }
            return resultList;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService GetDocuments error on {collectionName}: {ex.Message}");
            return new List<T>();
        }
    }

    public async Task<List<T>> GetUserDocumentsAsync<T>(string collectionName, string userId)
    {
        try
        {
            var jsonElementList = await _js.InvokeAsync<List<JsonElement>>("firebaseHelper.getUserDocuments", collectionName, userId);
            var resultList = new List<T>();
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            if (jsonElementList != null)
            {
                foreach (var elem in jsonElementList)
                {
                    var item = JsonSerializer.Deserialize<T>(elem.GetRawText(), options);
                    if (item != null)
                    {
                        resultList.Add(item);
                    }
                }
            }
            return resultList;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService GetUserDocuments error on {collectionName}: {ex.Message}");
            return new List<T>();
        }
    }

    public async Task<bool> SetDocumentsBatchAsync<T>(string collectionName, List<(string Id, T Data)> documents)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var batchData = documents.Select(d => new
            {
                id = d.Id,
                data = d.Data
            }).ToList();
            var json = JsonSerializer.Serialize(batchData, options);
            return await _js.InvokeAsync<bool>("firebaseHelper.setDocumentsBatch", collectionName, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FirebaseService SetDocumentsBatch error on {collectionName}: {ex.Message}");
            return false;
        }
    }
}

public class FirebaseUser
{
    public string Uid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class FirebaseResult
{
    public bool Success { get; set; }
    public string? Uid { get; set; }
    public string? Email { get; set; }
    public string? Error { get; set; }
}
