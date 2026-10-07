using System;using System.Collections;using System.Collections.Generic;using System.Reflection;
struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}public static Vector2 operator*(Vector2 v,float f)=>new(v.x*f,v.y*f);}
struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public static Vector3 one=>new(1,1,1);public static Vector3 operator*(Vector3 v,float f)=>new(v.x*f,v.y*f,v.z*f);public static Vector3 operator/(Vector3 v,float f)=>v*(1/f);}
class Transform { public Vector3 localScale=Vector3.one; }
class RectTransform:Transform {public Vector2 anchoredPosition;}
class Component {public Transform transform=new RectTransform();}
static class Plugin {public static void Debug(string s){} }
static class AccessTools {public static PropertyInfo Property(Type t,string n)=>t.GetProperty(n);public static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);public static MethodInfo Method(Type t,string n,Type[] args=null)=>args==null?t.GetMethod(n):t.GetMethod(n,args);public static Type TypeByName(string n)=>n=="DG.Tweening.DOTween"?typeof(Tween):typeof(Screen);}
static class Resources {public static Screen Owner;public static object[] FindObjectsOfTypeAll(Type t)=>new object[]{Owner};}
static class Tween {public static List<(object target,Action action)> Pending=new(); public static int Kill(object target,bool complete){foreach(var t in Pending.ToArray())if(ReferenceEquals(t.target,target)){if(complete)t.action();Pending.Remove(t);}return 0;}public static void Advance(){foreach(var t in Pending.ToArray())t.action();Pending.Clear();}}
class Screen {public View _mapView;public float _targetZoom=9;public Vector2 _savedMainMapPos=new(999,999);}
class View:Component {
 public float ZoomMain{get;set;}=.5f;public float ZoomMini{get;set;}=.7f;public float ZoomMin{get;set;}=.5f;public float ZoomMax{get;set;}=10;
 public bool IsMiniMapActive{get;set;} public Vector2 MainMapPos{get;private set;}=new(10,20); public Vector2 _immediateMapAnchor;
 public List<Component> _markers=new(){new Component()};public List<Component> _labels=new(){new Component()};
 public void SetMapZoom(float z,float time,bool main,bool mini){z=Math.Clamp(z,ZoomMin,ZoomMax);if(main){if(z==ZoomMain)return;ZoomMain=z;}if(mini){if(z==ZoomMini)return;ZoomMini=z;}float active=IsMiniMapActive?ZoomMini:ZoomMain;Tween.Pending.Add((transform,()=>transform.localScale=Vector3.one*active));foreach(var c in _markers)Tween.Pending.Add((c.transform,()=>c.transform.localScale=Vector3.one/active));foreach(var c in _labels)Tween.Pending.Add((c.transform,()=>c.transform.localScale=Vector3.one/active));}
}
class Program {
static MethodInfo _killTweenMethod;static Type _screenType;
class ViewportState {internal object MainZoom,MiniZoom;internal Vector2 MainPosition,VisiblePosition;internal bool IsMini;internal float DisplayedZoom;}
        private static ViewportState CaptureViewport(object candidate)
        {
            if (!(candidate is Component view) || !(view.transform is RectTransform rect))
                return null;
            var type = candidate.GetType();
            return new ViewportState
            {
                MainZoom = AccessTools.Property(type, "ZoomMain").GetValue(candidate),
                MiniZoom = AccessTools.Property(type, "ZoomMini").GetValue(candidate),
                MainPosition = (Vector2)AccessTools.Property(type, "MainMapPos").GetValue(candidate),
                VisiblePosition = rect.anchoredPosition,
                IsMini = (bool)AccessTools.Property(type, "IsMiniMapActive").GetValue(candidate),
                DisplayedZoom = rect.localScale.x
            };
        }

        private static void RestoreViewport(object candidate, ViewportState state)
        {
            if (candidate == null || state == null) return;
            var type = candidate.GetType();
            StopViewTweens(candidate);
            var zoom = AccessTools.Method(type, "SetMapZoom");
            zoom.Invoke(candidate, new object[] { state.MainZoom, 0f, true, false });
            zoom.Invoke(candidate, new object[] { state.MiniZoom, 0f, false, true });
            var mainZoom = (float)AccessTools.Property(type, "ZoomMain").GetValue(candidate);
            var miniZoom = (float)AccessTools.Property(type, "ZoomMini").GetValue(candidate);
            var activeZoom = state.IsMini ? miniZoom : mainZoom;
            var visiblePosition = state.VisiblePosition * ZoomRatio(activeZoom, state.DisplayedZoom);
            // Public zoom setters schedule DOTweens. Cancel them without completing stale
            // destinations, then commit the actual transform and marker scales immediately.
            StopViewTweens(candidate);
            var rect = (RectTransform)((Component)candidate).transform;
            rect.localScale = Vector3.one * activeZoom;
            rect.anchoredPosition = visiblePosition;
            var mainPosition = state.IsMini
                ? state.MainPosition * ZoomRatio(mainZoom, (float)state.MainZoom)
                : visiblePosition;
            AccessTools.Property(type, "MainMapPos").SetValue(candidate, mainPosition);
            AccessTools.Field(type, "_immediateMapAnchor")?.SetValue(candidate, visiblePosition);
            foreach (Transform child in MarkerTransforms(candidate))
                child.localScale = Vector3.one / activeZoom;
            SynchronizeScreen(candidate, mainPosition);
            Plugin.Debug($"Viewport committed: mini={state.IsMini}, mainZoom={mainZoom:F4}, miniZoom={miniZoom:F4}, displayed={rect.localScale.x:F4}, position={visiblePosition}");
        }

        private static IEnumerable MarkerTransforms(object candidate)
        {
            var type = candidate.GetType();
            foreach (var name in new[] { "_markers", "_labels" })
            {
                if (!(AccessTools.Field(type, name)?.GetValue(candidate) is IEnumerable entries)) continue;
                foreach (var entry in entries)
                    if (entry is Component component && component != null)
                        yield return component.transform;
            }
        }

        private static void StopViewTweens(object candidate)
        {
            _killTweenMethod ??= AccessTools.Method(AccessTools.TypeByName("DG.Tweening.DOTween"),
                "Kill", new[] { typeof(object), typeof(bool) });
            if (_killTweenMethod == null) throw new MissingMethodException("DOTween.Kill(object,bool)");
            _killTweenMethod.Invoke(null, new object[] { ((Component)candidate).transform, false });
            foreach (var child in MarkerTransforms(candidate))
                _killTweenMethod.Invoke(null, new object[] { child, false });
        }

        private static void SynchronizeScreen(object mapView, Vector2 mainPosition)
        {
            var screenType = (_screenType ??= AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen"));
            if (screenType == null) return;
            var viewField = AccessTools.Field(screenType, "_mapView");
            foreach (var screen in Resources.FindObjectsOfTypeAll(screenType))
                if (ReferenceEquals(viewField?.GetValue(screen), mapView))
                {
                    AccessTools.Field(screenType, "_targetZoom")?.SetValue(screen, 0f);
                    AccessTools.Field(screenType, "_savedMainMapPos")?.SetValue(screen, mainPosition);
                }
        }

        private static float ZoomRatio(float current, float previous)
            => previous > 0f ? current / previous : 1f;


static void Near(float a,float b){if(Math.Abs(a-b)>.0001)throw new Exception($"{a}!={b}");}
static void Main(){int n=0;foreach(bool mini in new[]{false,true})foreach(float min in new[]{.5f,1f})foreach(bool animated in new[]{false,true}){
var view=new View{IsMiniMapActive=mini,ZoomMin=min};var rect=(RectTransform)view.transform;float old=mini?.7f:.5f,shown=animated?old*.6f:old;rect.localScale=Vector3.one*shown;rect.anchoredPosition=new(30,40);Resources.Owner=new Screen{_mapView=view};
Tween.Pending.Add((rect,()=>{rect.localScale=Vector3.one*9;rect.anchoredPosition=new(900,900);}));foreach(var c in view._markers)Tween.Pending.Add((c.transform,()=>c.transform.localScale=Vector3.one*9));foreach(var c in view._labels)Tween.Pending.Add((c.transform,()=>c.transform.localScale=Vector3.one*9));
var snapshot=CaptureViewport(view);RestoreViewport(view,snapshot);Tween.Advance();
float main=Math.Max(.5f,min),small=Math.Max(.7f,min),active=mini?small:main,ratio=active/shown;
Near(rect.localScale.x,active);Near(rect.anchoredPosition.x,30*ratio);Near(rect.anchoredPosition.y,40*ratio);Near(view._immediateMapAnchor.x,30*ratio);Near(view.MainMapPos.x,mini?10*main/.5f:30*ratio);Near(Resources.Owner._targetZoom,0);Near(Resources.Owner._savedMainMapPos.x,view.MainMapPos.x);foreach(var c in view._markers)Near(c.transform.localScale.x,1/active);foreach(var c in view._labels)Near(c.transform.localScale.x,1/active);n++;
}Console.WriteLine($"PASS: {n} production viewport transactions: main/mini, clamp/no clamp, interrupted/completed scale, stale root/marker/label tweens and scroll targets.");}}
