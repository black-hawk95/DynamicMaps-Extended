const fs = require('fs');
const src = fs.readFileSync(require('path').resolve(__dirname,'../src/Assets/RasterSpritePatch.cs'),'utf8');
const method = src.slice(src.indexOf('        internal static bool NeedsNativeRefresh('), src.indexOf('        internal static bool EnsureLayerSprite('));
let harness = fs.readFileSync('work/regression/Program.cs','utf8');
harness = harness.replace('static void Main() {', `${method}
class FakeSprite { public string name; }
static bool _override;
static string _path="vector.svg";
static Dictionary<string,FakeSprite> SvgOverrideCache=new();
static string GetLayerImagePath(object layer)=>_path;
static object GetLayerDef(object layer)=>layer;
static string SvgOverrideCacheKey(object layer,string path)=>path;
static bool ShouldLoadSvgOverride(string source,string path)=>_override;
class Resolution { public string ResolvedPath; }
static class MapStyleManager { public static Resolution Resolve(string path)=>new(){ResolvedPath=path}; }

static FakeSprite GetLayerSprite(object layer) => layer as FakeSprite;
static void Main() {
int styleCases=0;
foreach(var test in new (string name,bool expected)[]{
("DMExt:Woods-Ground.png",true), ("DMExtWarm:Woods-Ground.warm.png",true),
("DMExtWarm:Interchange-Ground.warm.png",true), ("dmextwarm:test",true),
("DMExtSvgOverride:Interchange-Ground_Level.svg",true),
("DMExt:deferred-raster-placeholder",true), ("Woods-Ground_Level",false),
("",false), (null,true)}) {
object layer=test.name==null ? null : new FakeSprite{name=test.name};
if(NeedsNativeRefresh(layer)!=test.expected) throw new Exception("Native restoration failed for "+test.name);
styleCases++;
}
Console.WriteLine($"PASS: {styleCases} native/warm/SVG sprite restoration regression cases using production method.");
`);
harness=harness.replace('Console.WriteLine($"PASS: {styleCases}',`_override=true;
var vector=new FakeSprite{name="DMExtSvgOverride:vector.svg"};
SvgOverrideCache["vector.svg"]=vector;
for(int tick=0;tick<600;tick++)if(NeedsNativeRefresh(vector))throw new Exception("Cached vector was repeatedly refreshed");
if(!NeedsNativeRefresh(new FakeSprite{name=vector.name}))throw new Exception("Wrong sprite identity accepted");
_path="other.svg";
if(!NeedsNativeRefresh(vector))throw new Exception("Stale style accepted");
_path="vector.svg";
if(!NeedsNativeRefresh(new FakeSprite{name="native"}))throw new Exception("Hidden native floor did not change to SVG");
_override=false;
if(!NeedsNativeRefresh(vector))throw new Exception("Vanilla restoration missed");
styleCases+=5;
Console.WriteLine($"PASS: {styleCases}`);
fs.writeFileSync('work/regression/Program.cs',harness);
