const fs = require('fs');
const source = fs.readFileSync(require('path').resolve(__dirname,'../src/Assets/TilePackManager.cs'), 'utf8');
const ring = source.slice(source.indexOf('            var tileLimit = DesiredTileLimit'), source.indexOf('        internal static int DesiredTileLimit')).replace(/\s*}\s*$/, '');
const limit = source.slice(source.indexOf('        internal static int DesiredTileLimit'), source.indexOf('        private static void AddVisibleTile'));
const add = source.slice(source.indexOf('        private static void AddVisibleTile'), source.indexOf('        private static RectTransform FindViewportMask'));
fs.mkdirSync('work/regression', {recursive:true});
fs.writeFileSync('work/regression/regression.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><UseAppHost>false</UseAppHost></PropertyGroup><ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>');
fs.writeFileSync('work/regression/Program.cs', `using System; using System.Collections.Generic; using System.Linq;
class Program {
const int MaxDesiredTiles=160; const long MaxCacheBytes=64L*1024*1024;
${limit}
class Record { public int Length; }
class Pack { public int Columns, Rows; public int TileSize=256; public Record[] Tiles; }
class LayerState { public Pack Pack; public HashSet<int> DesiredSet=new(); }
${add}
static void Scan(LayerState state, List<int> result) {
int colMin=0, rowMin=0, colMax=state.Pack.Columns-1, rowMax=state.Pack.Rows-1;
${ring}
}
static void Main() {
int cases=0;
foreach(int tileSize in new[]{256,512,1024}) foreach(int width in new[]{1,2,3,4,8,12,32}) foreach(int height in new[]{1,2,3,4,8,12,32}) foreach(bool holes in new[]{false,true}) {
var state=new LayerState { Pack=new Pack {Columns=width,Rows=height,TileSize=tileSize,Tiles=Enumerable.Range(0,width*height).Select(i=>new Record {Length=holes && i%3==0 ? 0 : 100}).ToArray()} };
var result=new List<int>(); Scan(state,result);
int expected=Math.Min(DesiredTileLimit(tileSize),state.Pack.Tiles.Count(x=>x.Length>0));
if(result.Count!=expected || result.Distinct().Count()!=result.Count || !state.DesiredSet.SetEquals(result) || result.Any(i=>i<0 || i>=width*height || state.Pack.Tiles[i].Length==0)) throw new Exception($"Invalid selection {width}x{height}, holes={holes}: {result.Count}/{expected}");
cases++;
}
Console.WriteLine($"PASS: {cases} edge-clipped, sparse and capped tile selection cases using extracted production code.");
}}
`);
