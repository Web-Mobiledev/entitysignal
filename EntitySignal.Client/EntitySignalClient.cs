﻿using EntitySignal.Client.Enums;
using EntitySignal.Client.Extensions;
using EntitySignal.Client.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace EntitySignal.Client
{
  public class EntitySignalClient
  {
    private SyncSubscription _subscriptions = new SyncSubscription();
    private HubConnection _hub;
    private readonly EntitySignalOptions _options = new EntitySignalOptions();
    private TaskCompletionSource<PromiseOutput<Exception>> _connectingDefer;
    private string _connectionId;

    public delegate void OnStatusChangedEvent(EntitySignalStatus status);
    public delegate void OnSyncEvent(UserResult newData);

    public event OnStatusChangedEvent OnStatusChanged;
    public event OnSyncEvent OnSync;
    private Dictionary<string, List<Action<JArray>>> UrlDataChangeCallbacks = new Dictionary<string, List<Action<JArray>>>();

    private EntitySignalStatus _status = EntitySignalStatus.Disconnected;

    public EntitySignalStatus Status
    {
      get => _status;
      set
      {
        _status = value;
        OnStatusChanged?.Invoke(_status);
      }
    }

    public EntitySignalClient(EntitySignalOptions options = null,
        Func<HttpMessageHandler, HttpMessageHandler> httpMessageHandlerFactory = null)
    {
      //if (options != null)
      {
        _options = options;
      }

      _hub = new HubConnectionBuilder()
          .WithUrl(_options.HubUrl, connectionOption =>
          {
            connectionOption.HttpMessageHandlerFactory = httpMessageHandlerFactory;
          })
          .Build();

      _hub.Closed += OnClose;
      _hub.On<UserResult>("Sync", data =>
      {
        if (!_options.SuppressInternalDataProcessing)
        {
          ProcessSync(data);
        }

        OnSync?.Invoke(data);

        data.Urls.GroupBy(x => x.Url).Select(g => g.LastOrDefault()).ToList().ForEach(x =>
              {
                var urlCallbacks = UrlDataChangeCallbacks.FirstOrDefault(ud => ud.Key.EndsWith(x.Url)).Value;

                if (urlCallbacks == null)
                {
                  return;
                }

                urlCallbacks.ForEach(callback =>
                      {
                        if (_options.ReturnDeepCopy)
                        {
                          callback(GetSubscription(x).DeepClone() as JArray);
                        }
                        else
                        {
                          callback(GetSubscription(x));
                        }
                      });
              });
      });
    }

    public void OnDataChange(string url, Action<JArray> action)
    {
      var urlDataChangeCallback = UrlDataChangeCallbacks.ContainsKey(url)
          ? UrlDataChangeCallbacks[url]
          : null;

      if (urlDataChangeCallback == null)
      {
        UrlDataChangeCallbacks[url] = new List<Action<JArray>>();
      }
      UrlDataChangeCallbacks[url].Add(action);
    }

    public void OffDataChange(string url, Action<JArray> action)
    {
      var urlDataChangeCallback = UrlDataChangeCallbacks.ContainsKey(url)
          ? UrlDataChangeCallbacks[url]
          : null;

      if (urlDataChangeCallback != null && urlDataChangeCallback.Contains(action))
      {
        urlDataChangeCallback.Remove(action);
      }
    }

    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2 arg2, X509Chain arg3, SslPolicyErrors arg4)
    {
      // TODO: You can do custom validation here, or just return true to always accept the certificate.
      // DO NOT use custom validation logic in a production application as it is insecure.
      return true;
    }

    private Task OnClose(Exception arg)
    {
      Status = EntitySignalStatus.Disconnected;

      return Reconnect();
    }

    private void DebugPrint(string output)
    {
      Debug.WriteLineIf(_options.Debug, output);
    }

    private async Task Connect()
    {
      if (Status == EntitySignalStatus.Connected)
      {
        return;
      }

      if (Status == EntitySignalStatus.Connecting ||
          Status == EntitySignalStatus.WaitingForConnectionId)
      {
        await _connectingDefer.Task;
      }

      DebugPrint("Connecting");

      if (Status == EntitySignalStatus.Disconnected)
      {
        Status = EntitySignalStatus.Connecting;
        _connectingDefer = new TaskCompletionSource<PromiseOutput<Exception>>();

        _hub.On<string>("ConnectionIdChanged", connectionId =>
        {
          //this should be a one shot so just remove handler after first use
          _hub.Remove("ConnectionIdChanged");

          Status = EntitySignalStatus.Connected;
          _connectionId = connectionId;

          DebugPrint("Connected");

          _connectingDefer.SetResult(new PromiseOutput<Exception>(true));
        });

        try
        {
          await _hub.StartAsync();

          if (Status == EntitySignalStatus.Connecting)
          {
            DebugPrint("Connected, waiting for connectionId");
            Status = EntitySignalStatus.WaitingForConnectionId;
          }

          await Task.Delay(_options.MaxWaitForConnectionId);

          if (Status == EntitySignalStatus.WaitingForConnectionId)
          {
            _connectingDefer.SetResult(new PromiseOutput<Exception>(false));
          }
        }
        catch (Exception ex)
        {
          DebugPrint("Error Connecting");
          Status = EntitySignalStatus.Disconnected;
          _connectingDefer.SetResult(new PromiseOutput<Exception>(false, ex));

          Debug.WriteLine(ex.ToString());
        }

        await _connectingDefer.Task;
      }
    }

    private async Task Reconnect()
    {
      if (!_options.AutoReconnect)
      {
        return;
      }

      DebugPrint("Reconnecting");

      await Connect();

      if (Status == EntitySignalStatus.Connected)
      {
        DebugPrint("Reconnect Success");

        foreach (var index in _subscriptions)
        {
          await HardRefresh(index.Key);
        }
      }
      else
      {
        var random = new Random();
        DebugPrint("Reconnect Failed");
        var reconnectTime = _options.ReconnectMinTime + (random.NextDouble() * _options.ReconnectVariance);
        DebugPrint("Attempting reconnect in " + reconnectTime + "ms");
        await Task.Delay((int)reconnectTime);
        await Reconnect();
      }
    }

    public List<string> ProcessSync(UserResult data)
    {
      var changedUrls = new List<string>();

      data.Urls.ForEach(url =>
      {
        changedUrls.Add(url.Url);

        url.Data.ForEach(x =>
              {
                if (x.State == EntityState.Added ||
                          x.State == EntityState.Modified)
                {
                  var changeCount = 0;
                  var subscription = GetSubscription(url);

                  if (subscription == null)
                  {
                    return;
                  }

                  subscription.ForEach((msg, index) =>
                        {
                          if (x.Object[_options.DefaultId] == msg[_options.DefaultId] || //check default ID type
                                x.Object[_options.DefaultIdAlt] == msg[_options.DefaultIdAlt]) //check alt ID type
                          {
                            GetSubscription(url)[index].Replace(x.Object);
                            changeCount++;
                          }
                        });

                  if (changeCount == 0)
                  {
                    GetSubscription(url).Add(x.Object);
                  }
                }
                else if (x.State == EntityState.Deleted)
                {
                  var subscription = GetSubscription(url);

                  if (subscription == null)
                  {
                    return;
                  }

                  for (var i = subscription.Count - 1; i >= 0; i--)
                  {
                    var currentRow = GetSubscription(url)[i];

                    if (currentRow == null)
                    {
                      return;
                    }

                    //check default ID type
                    if (x.Object[_options.DefaultId] == currentRow[_options.DefaultId] || //check default ID type
                        x.Object[_options.DefaultIdAlt] == currentRow[_options.DefaultIdAlt]) //check alt ID type
                    {
                      GetSubscription(url)?.RemoveAt(i);
                    }
                  }
                }
              });
      });

      return changedUrls;
    }

    private JArray GetSubscription(UserUrlResult userUrlResult)
    {
      return _subscriptions.FirstOrDefault(s => s.Key.EndsWith(userUrlResult.Url)).Value;
    }

    private async Task<PromiseOutput<string>> DesyncFrom(string url)
    {
      var promise = new TaskCompletionSource<PromiseOutput<string>>();

      try
      {
        await _hub.InvokeAsync("DeSyncFrom", url);
        promise.SetResult(new PromiseOutput<string>(true));
      }
      catch (Exception ex)
      {
        promise.SetResult(new PromiseOutput<string>(false, ex.ToString()));
      }

      return await promise.Task;
    }

    private async Task<PromiseOutput<JArray, Exception>> HardRefresh(string url)
    {
      var promise = new TaskCompletionSource<PromiseOutput<JArray, Exception>>();

      await Connect();

      if (Status == EntitySignalStatus.Connected)
      {
        var httpClient = new HttpClient();

        var requestMessage = new HttpRequestMessage
        {
          RequestUri = new Uri(url),
          Method = HttpMethod.Get
        };

        requestMessage.Headers.Add("x-signalr-connection-id", _connectionId);

        var response = await httpClient.SendAsync(requestMessage);
        var json = await response.Content.ReadAsStringAsync();
        var data = JArray.Parse(json);

        if (response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NoContent)
        {
          _subscriptions[url] = data;

          if (_options.ReturnDeepCopy)
          {
            promise.SetResult(new PromiseOutput<JArray, Exception>(true, data.DeepClone() as JArray));
          }
          else
          {
            promise.SetResult(new PromiseOutput<JArray, Exception>(true, data));
          }
        }
        else
        {
          promise.SetResult(new PromiseOutput<JArray, Exception>(false, failResult: new Exception(response.ReasonPhrase)));
        }
      }

      return await promise.Task;
    }

    public async Task<PromiseOutput<JArray, Exception>> SyncWith(string url)
    {
      //if already subscribed to then return array
      if (_subscriptions.ContainsKey(url) && _subscriptions[url] != null)
      {
        return new PromiseOutput<JArray, Exception>(true, _subscriptions[url]);
      }

      return await HardRefresh(url);
    }
  }
}
