using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using TelegramNotifier;
int checks=0;void Check(bool ok,string label){checks++;if(!ok)throw new Exception("FAIL "+label);}
var folder=Path.Combine(Path.GetTempPath(),"telegram-interval-test-"+Guid.NewGuid());Directory.CreateDirectory(folder);var path=Path.Combine(folder,"telegram_notifier.jsonc");
var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../TelegramNotifier"));
var listeners=new Dictionary<string,List<Delegate>>();var timers=new List<FakeTimer>();double now=0;
var sent=new ConcurrentQueue<(long Chat,string Text)>();var people=new Dictionary<int,IPlayer>();int oneSecondTimers=0;
IPlayer Player(int id,ulong steam,bool bot=false)=>Proxy.Make<IPlayer>((m,a)=>m.Name switch{
 "get_PlayerID"=>id,"get_SteamID"=>steam,"get_UnauthorizedSteamID"=>steam,"get_IsFakeClient"=>bot,"get_IsValid"=>true,"get_Name"=>"<Player & "+id+">",_=>Proxy.Default(m,Handle)});
object? Handle(MethodInfo m,object?[]? a)
{
 if(m.Name=="get_Logger")return NullLogger.Instance;
 if(m.Name=="get_PluginPath")return source;
 if(m.Name=="InitializeJsonWithModel")return Proxy.Make(m.ReturnType,Handle);
 if(m.Name=="GetConfigPath")return path;
 if(m.Name=="get_GlobalVars")throw new NotSupportedException("No native world in test host");
 if(m.Name=="FindAsString")return null;
 if(m.Name=="GetAllValidPlayers")return people.Values.ToList();
 if(m.Name=="get_MaxPlayers")return 26;
 if(m.Name=="GetPlayer")return people.GetValueOrDefault((int)a![0]!);
 if(m.Name=="DelayAndRepeatBySeconds"){var c=new CancellationTokenSource();timers.Add(new(c.Token,(Action)a![2]!,now+(float)a[0]!, (float)a[1]!));return c;}
 if(m.Name=="DelayBySeconds"){oneSecondTimers++;throw new Exception("Unexpected event-triggered status timer");}
 if(m.Name.StartsWith("add_")){var key=m.Name[4..];if(!listeners.TryGetValue(key,out var l))listeners[key]=l=[];l.Add((Delegate)a![0]!);return null;}
 if(m.Name.StartsWith("remove_")){if(listeners.TryGetValue(m.Name[7..],out var l))l.Remove((Delegate)a![0]!);return null;}
 return Proxy.Default(m,Handle);
}
void Config(int? minutes=5,int? seconds=null,bool unchanged=false,bool status=true,bool connections=true,bool wrapped=true)
{
 var server=new Dictionary<string,object?>{{"Enabled",status},{"ChatId",101L},{"SendUnchangedPeriodicStatus",unchanged},{"SendOnPlayerCountChange",true}};
 if(minutes.HasValue)server["SendIntervalMinutes"]=minutes.Value;if(seconds.HasValue)server["SendIntervalSeconds"]=seconds.Value;
 var root=new Dictionary<string,object?>{{"Version",2},{"Language","uk"},{"BotToken","OFFLINE_TEST_TOKEN_NOT_REAL"},{"ServerStatus",server},{"PlayerConnections",new{Enabled=connections,ChatId=202L,IgnoreBots=true}}};
 File.WriteAllText(path,JsonSerializer.Serialize(wrapped?new Dictionary<string,object?>{{"TelegramNotifier",root}}:root));
}
void Event<T>(string name,Func<MethodInfo,object?[]?,object?> fields)where T:class
{var e=Proxy.Make<T>(fields);foreach(var d in listeners.GetValueOrDefault(name,[]).ToArray())d.DynamicInvoke(e);}
void Join(int id,ulong steam,bool bot=false){people[id]=Player(id,steam,bot);Event<IOnClientSteamAuthorizeEvent>("OnClientSteamAuthorize",(m,a)=>m.Name=="get_PlayerId"?id:Proxy.Default(m,Handle));}
void Map(string map)=>Event<IOnMapLoadEvent>("OnMapLoad",(m,a)=>m.Name=="get_MapName"?map:Proxy.Default(m,Handle));
void Disconnect(int id){people.Remove(id);Event<IOnClientDisconnectedEvent>("OnClientDisconnected",(m,a)=>m.Name=="get_PlayerId"?id:Proxy.Default(m,Handle));}
void Advance(double seconds){now+=seconds;foreach(var timer in timers.ToArray())while(!timer.Token.IsCancellationRequested&&timer.Next<=now){timer.Next+=timer.Interval;timer.Action();}}
int Count(long id)=>sent.Count(x=>x.Chat==id);
async Task WaitCount(long id,int n){for(int i=0;i<100&&Count(id)<n;i++)await Task.Delay(5);Check(Count(id)==n,"expected HTTP message count "+id+"="+n);}
try
{
 var core=Proxy.Make<ISwiftlyCore>(Handle);var plugin=new TelegramNotifierPlugin(core,l=>new TelegramApiClient(l,new CaptureHandler(sent)));
 Config();plugin.Load(false);await WaitCount(101,1);Check(timers.Count==1&&timers[0].Interval==300&&timers[0].Next==300,"five minutes becomes a single 300-second timer");
 Join(1,76561198000000001);Join(2,76561198000000002);await WaitCount(202,2);Check(Count(101)==1,"joins do not push status immediately");
 Join(1,76561198000000001);await Task.Delay(10);Check(Count(202)==2,"player connection dedup unchanged");Check(sent.Any(x=>x.Chat==202&&x.Text.Contains("&lt;Player &amp;")),"player HTML escaping preserved");
 Join(9,999,true);Check(Count(202)==2,"bots ignored for player notifications");
 for(int i=0;i<10;i++){Map("de_dust2");Advance(1);Check(Count(101)==1,"map events do not send one-second statuses");}
 Map("de_overpass");Disconnect(1);Check(Count(101)==1,"disconnect does not send status");Check(oneSecondTimers==0,"no extra delayed event timers");
 Advance(289);Check(Count(101)==1,"no status before configured five minutes");Advance(1);await WaitCount(101,2);
 Check(sent.Last(x=>x.Chat==101).Text.Contains("de_overpass")&&sent.Last(x=>x.Chat==101).Text.Contains("1/26"),"timer uses latest map and online count; bots excluded");
 Advance(300);await Task.Delay(10);Check(Count(101)==2,"unchanged suppression retained");
 Map("de_nuke");Advance(299);Check(Count(101)==2,"map change waits for next interval");Advance(1);await WaitCount(101,3);Check(sent.Last(x=>x.Chat==101).Text.Contains("de_nuke"),"new map included at next interval");
 var old=timers[0];plugin.Unload();Check(old.Token.IsCancellationRequested,"unload cancels timer");Check(listeners.Values.All(x=>x.Count==0),"unload unsubscribes all events");
 int before=Count(101);old.Action();Advance(600);await Task.Delay(10);Check(Count(101)==before,"queued old timer inert after unload");
 Config(2,unchanged:true);plugin.Load(true);await WaitCount(101,before+1);Check(timers.Last().Interval==120,"two-minute config honored");
 before=Count(101);old.Action();await Task.Delay(10);Check(Count(101)==before,"old-load timer cannot send through a new load");
 Advance(120);await WaitCount(101,before+1);Advance(120);await WaitCount(101,before+2);Check(sent.Last(x=>x.Chat==101).Text==sent.Where(x=>x.Chat==101).Reverse().Skip(1).First().Text,"optional unchanged periodic statuses sent on schedule");
 foreach(var spec in new[]{(Minutes:(int?)null,Seconds:(int?)90,Expected:120f),(Minutes:(int?)3,Seconds:(int?)15,Expected:180f),(Minutes:(int?)0,Seconds:(int?)null,Expected:60f),(Minutes:(int?)5000,Seconds:(int?)null,Expected:86400f)})
 {plugin.Unload();Config(spec.Minutes,spec.Seconds);plugin.Load(false);Check(timers.Last().Interval==spec.Expected,"legacy/precedence/clamp interval "+spec.Expected);}
 plugin.Unload();Config(minutes:7,wrapped:false);plugin.Load(false);Check(timers.Last().Interval==420,"flat legacy example config supported");
 plugin.Unload();Config(minutes:4);File.WriteAllText(path,File.ReadAllText(path).Replace("TelegramNotifier","telegramNotifier").Replace("ServerStatus","serverStatus").Replace("SendIntervalMinutes","sendIntervalMinutes"));plugin.Load(false);Check(timers.Last().Interval==240,"case-insensitive existing config remains supported");
 plugin.Unload();before=Count(101);int timerCount=timers.Count;Config(status:false);plugin.Load(false);Check(timers.Count==timerCount,"disabled server status makes no timer");await Task.Delay(10);Check(Count(101)==before,"disabled status sends no startup message");
 int joined=Count(202);Join(3,76561198000000003);await WaitCount(202,joined+1);Check(Count(101)==before,"player notifications unaffected by status disable");plugin.Unload();
 // Exercise actual delivery logic without any network.
 var delivery=new StatusDelivery();int attempts=0;
 await delivery.SendAsync("A",false,_=>{attempts++;return Task.FromResult(false);},default);
 await delivery.SendAsync("A",false,_=>{attempts++;return Task.FromResult(true);},default);Check(attempts==2,"failed status is retried at next scheduled call");
 await delivery.SendAsync("A",false,_=>{attempts++;return Task.FromResult(true);},default);Check(attempts==2,"successful duplicate skipped");
 await delivery.SendAsync("A",true,_=>{attempts++;return Task.FromResult(true);},default);Check(attempts==3,"forced periodic unchanged status works");
 var held=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var active=delivery.SendAsync("B",false,_=>held.Task,default);
 await delivery.SendAsync("C",false,_=>{attempts++;return Task.FromResult(true);},default);Check(attempts==3,"overlapping HTTP send skipped, not marked successful");held.SetResult(true);await active;
 await delivery.SendAsync("C",false,_=>{attempts++;return Task.FromResult(true);},default);Check(attempts==4,"latest skipped snapshot can send at later interval");
 using var cancel=new CancellationTokenSource();cancel.Cancel();await delivery.SendAsync("D",true,_=>throw new Exception("must not send"),cancel.Token);Check(true,"cancellation safely observed");
 Check(typeof(TelegramNotifierPlugin).Assembly.GetName().Version==new Version(1,1,1,0),"assembly metadata/version readable");
 Console.WriteLine($"PASS: {checks} assertions: actual plugin timers/events with fake Swiftly and intercepted HTTP; minute interval, no event bypass, latest snapshots, connection notifications, legacy config, lifecycle, cancellation and retry. No requests sent to Telegram; no live CS2 test.");
}
finally{Directory.Delete(folder,true);}
sealed class FakeTimer(CancellationToken token,Action action,double next,float interval){public CancellationToken Token=token;public Action Action=action;public double Next=next;public float Interval=interval;}
sealed class CaptureHandler(ConcurrentQueue<(long Chat,string Text)> sent):HttpMessageHandler
{
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));sent.Enqueue((doc.RootElement.GetProperty("chat_id").GetInt64(),doc.RootElement.GetProperty("text").GetString()!));return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true}")};}
}
public class Proxy:DispatchProxy
{
 public Func<MethodInfo,object?[]?,object?> Handler=null!;
 public static object Make(Type type,Func<MethodInfo,object?[]?,object?> f){var p=Create(type,typeof(Proxy));((Proxy)p).Handler=f;return p;}
 public static T Make<T>(Func<MethodInfo,object?[]?,object?> f)where T:class=>(T)Make(typeof(T),f);
 protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);
 public static object? Default(MethodInfo m,Func<MethodInfo,object?[]?,object?> f){var t=m.ReturnType;if(t==typeof(void))return null;if(t.IsInterface)return Make(t,f);return t.IsValueType?Activator.CreateInstance(t):null;}
}
