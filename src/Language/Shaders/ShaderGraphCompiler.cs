using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Veldrid.SPIRV;

namespace KNE;

/// <summary>An exposed shader argument, keyed by a stable file and block identity.</summary>
public sealed record ExposedShaderParameter(string Id, string Name, ScriptNodeKind Kind, double[] Values, double Minimum, double Maximum);
/// <summary>Portable source and the presentation target of a validated shader graph.</summary>
public sealed record CompiledShaderGraph(string Glsl, string Target, string Screen, bool Animated, IReadOnlyList<ExposedShaderParameter> Parameters)
{
    /// <summary>Translates the same vertex/fragment pair to HLSL using SPIRV-Cross.</summary>
    public string ToHlsl() => SpirvCompilation.CompileVertexFragment(Encoding.UTF8.GetBytes(NativeGraphicsRenderer.VertexShader), Encoding.UTF8.GetBytes(Glsl), CrossCompileTarget.HLSL).FragmentShader;
}

/// <summary>Compiles bounded acyclic data-flow graphs to portable fragment source.</summary>
public static class ShaderGraphCompiler
{
    public static VisualScript Create()
    {
        var texture = BlockLibrary.Create(ScriptNodeKind.ShaderTexture); texture.Id = "texture"; texture.X = 60; texture.Y = 80;
        var output = BlockLibrary.Create(ScriptNodeKind.ShaderOutput); output.Id = "output"; output.X = 430; output.Y = 80;
        output.Shader!.Inputs["Color"] = new(texture.Id);
        return new() { IsShader = true, FunctionName = "shader", Nodes = [texture, output] };
    }
    public static CompiledShaderGraph Compile(VisualScript graph, string root, string? sourcePath = null, IReadOnlyDictionary<string, double[]>? exposed = null)
    {
        ArgumentNullException.ThrowIfNull(graph); _ = graph.Serialize();
        if (!graph.IsShader) throw new InvalidDataException("Ожидается шейдерная блок-схема .kneshader.");
        var source = new StringBuilder(Header); var parameters = new List<ExposedShaderParameter>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourcePath != null) files.Add(PathSafety.EnsureInside(root, sourcePath));
        int serial = 0, total = 0; bool animated = false; int samples = 0;
        var samplingCosts = new Dictionary<string, long>(StringComparer.Ordinal);
        string CompileGraph(VisualScript model, string identity, int depth)
        {
            if (depth > 12 || (total += model.Nodes.Count) > 500) throw new InvalidDataException("Шейдер: предел 12 вложений / 500 блоков с подсистемами.");
            _ = model.Serialize();
            if (!model.IsShader) throw new InvalidDataException("Контейнер должен ссылаться на .kneshader.");
            var map = model.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var outputs = model.Nodes.Where(node => node.Kind == ScriptNodeKind.ShaderOutput).ToArray();
            if (outputs.Length != 1) throw new InvalidDataException("В шейдере должен быть ровно один Fragment Output.");
            var names = map.Keys.ToDictionary(id => id, _ => "kne_n" + serial++, StringComparer.Ordinal);
            var done = new HashSet<string>(); var active = new HashSet<string>();
            foreach (var node in model.Nodes)
                foreach (var link in node.Shader!.Inputs.Values)
                    if (!map.TryGetValue(link.Node, out var from) || link.Output >= ShaderLibrary.Get(from.Kind).Outputs.Length)
                        Fail(node, "вход ссылается на отсутствующий блок / выход");
            string Visit(ScriptNode node)
            {
                if (done.Contains(node.Id)) return names[node.Id];
                if (!active.Add(node.Id)) Fail(node, "цикл значений; обратные связи шейдера не поддерживаются");
                foreach (var link in node.Shader!.Inputs.Values) Visit(map[link.Node]);
                string N(double value) => value.ToString("0.0################", CultureInfo.InvariantCulture);
                string P(string key) => N(ShaderLibrary.Value(node, key));
                string I(string key, string uv = "uv")
                {
                    if (node.Shader.Inputs.TryGetValue(key, out var link))
                    {
                        string call = names[link.Node] + "(" + uv + ")";
                        return map[link.Node].Kind == ScriptNodeKind.ShaderSplit ? "vec4(" + call + "." + "rgba"[link.Output] + ")" : call;
                    }
                    return key switch {
                        "UV" => "vec4(" + uv + ",0.0,1.0)", "Time" => "vec4(kneFrame.x)",
                        "T" or "Edge" => "vec4(0.5)", "Min" => "vec4(0.0)", "R" or "G" or "B" when node.Kind == ScriptNodeKind.ShaderCombine => "vec4(0.0)", "Max" => "vec4(1.0)", "A" when node.Kind == ScriptNodeKind.ShaderCombine => "vec4(1.0)",
                        "A" when node.Kind is ScriptNodeKind.ShaderLinearGradient or ScriptNodeKind.ShaderRadialGradient => "vec4(0.0,0.0,0.0,1.0)",
                        "B" when node.Kind is ScriptNodeKind.ShaderLinearGradient or ScriptNodeKind.ShaderRadialGradient => "vec4(1.0)",
                        "B" => "vec4(0.5)", _ => "kne_sample(" + uv + ")" };
                }
                string color = I("Color"), coord = "(" + I("UV") + ").xy";
                string body; int loopSamples = 1;
                switch (node.Kind)
                {
                    case ScriptNodeKind.ShaderContainer:
                        if (!ScriptDocument.IsShaderPath(node.Path)) Fail(node, "выберите файл .kneshader");
                        string path = PathSafety.ResolveRelative(root, node.Path);
                        if (!EngineLaunch.PluginsEnabled && EngineLaunch.IsPluginPath(node.Path)) Fail(node, "шейдерные контейнеры из plugins отключены безопасным режимом");
                        if (!files.Add(path)) Fail(node, "циклическое включение контейнеров");
                        if (new FileInfo(path).Length > 2 * 1024 * 1024) Fail(node, "файл больше 2 МБ");
                        var nested = VisualScript.Parse(File.ReadAllText(path));
                        string child = CompileGraph(nested, node.Path, depth + 1); files.Remove(path);
                        body = "return " + child + "(" + coord + ");"; break;
                    case ScriptNodeKind.ShaderTexture: body = "return kne_sample(" + coord + ");"; samples++; break;
                    case ScriptNodeKind.ShaderUv: body = "return vec4(uv,0,1);"; break;
                    case ScriptNodeKind.ShaderTime: animated = true; body = "return vec4(kneFrame.x);"; break;
                    case ScriptNodeKind.ShaderScreenPosition: body = "return vec4(kneOrigin.xy+uv*kneOrigin.zw,0,1);"; break;
                    case ScriptNodeKind.ShaderFloat:
                    case ScriptNodeKind.ShaderVector:
                    case ScriptNodeKind.ShaderColor:
                        string id = identity + "::" + node.Id;
                        double[] values = node.Kind == ScriptNodeKind.ShaderFloat ? [ShaderLibrary.Value(node, "x")] : new[] { "x", "y", "z", "w" }.Select(key => ShaderLibrary.Value(node, key)).ToArray();
                        double minimum = node.Kind == ScriptNodeKind.ShaderFloat ? ShaderLibrary.Value(node, "min") : node.Kind == ScriptNodeKind.ShaderColor ? 0 : -10;
                        double maximum = node.Kind == ScriptNodeKind.ShaderFloat ? ShaderLibrary.Value(node, "max") : node.Kind == ScriptNodeKind.ShaderColor ? 1 : 10;
                        if (exposed?.TryGetValue(id, out var replacement) == true)
                        {
                            if (replacement.Length != values.Length || replacement.Any(v => !double.IsFinite(v) || v < minimum || v > maximum)) Fail(node, "внешний параметр вне диапазона");
                            values = replacement;
                        }
                        parameters.Add(new(id, node.Shader.Name, node.Kind, values, minimum, maximum));
                        body = "return vec4(" + string.Join(",", values.Select(N)) + ");"; break;
                    case ScriptNodeKind.ShaderTilingOffset: body = $"return vec4({coord}*vec2({P("x")},{P("y")})+vec2({P("z")},{P("w")}),0,1);"; break;
                    case ScriptNodeKind.ShaderRotate: body = $"return vec4(kne_rotate({coord}-0.5,{P("amount")})+0.5,0,1);"; break;
                    case ScriptNodeKind.ShaderPolar: body = $"vec2 p={coord}-0.5; return vec4(length(p)*2.0,atan(p.y,p.x)/6.2831853+0.5,0,1);"; break;
                    case ScriptNodeKind.ShaderFlipbook:
                        body = $"vec2 cells=vec2({P("columns")},{P("rows")}); float f=mod(floor(({I("Time")}).x*{P("speed")}),cells.x*cells.y);return vec4((fract({coord})+vec2(mod(f,cells.x),floor(f/cells.x)))/cells,0,1);"; animated |= !node.Shader.Inputs.ContainsKey("Time"); break;
                    case ScriptNodeKind.ShaderTwirl: body = $"vec2 p={coord}-0.5;return vec4(kne_rotate(p,length(p)*{P("amount")})+0.5,0,1);"; break;
                    case ScriptNodeKind.ShaderSimplex: body = $"return vec4(vec3(kne_simplex({coord}*{P("scale")})*0.5+0.5),1);"; break;
                    case ScriptNodeKind.ShaderVoronoi: body = $"return vec4(vec3(kne_voronoi({coord}*{P("scale")})),1);"; break;
                    case ScriptNodeKind.ShaderCheckerboard: body = $"vec2 p=floor({coord}*{P("scale")});return vec4(vec3(mod(p.x+p.y,2.0)),1);"; break;
                    case ScriptNodeKind.ShaderLinearGradient: body = $"return mix({I("A")},{I("B")},clamp({coord}.x,0.0,1.0));"; break;
                    case ScriptNodeKind.ShaderRadialGradient: body = $"return mix({I("A")},{I("B")},clamp(length({coord}-0.5)*2.0,0.0,1.0));"; break;
                    case ScriptNodeKind.ShaderSplit: case ScriptNodeKind.ShaderOutput: body = "return " + color + ";"; break;
                    case ScriptNodeKind.ShaderCombine: body = $"return vec4(({I("R")}).x,({I("G")}).x,({I("B")}).x,({I("A")}).x);"; break;
                    case ScriptNodeKind.ShaderHueShift: body = $"vec4 c={color};vec3 k=vec3(0.57735027);float a={P("amount")}*6.2831853;c.rgb=c.rgb*cos(a)+cross(k,c.rgb)*sin(a)+k*dot(k,c.rgb)*(1.0-cos(a));return c;"; break;
                    case ScriptNodeKind.ShaderSaturation: body = $"vec4 c={color};c.rgb=mix(vec3(dot(c.rgb,vec3(.2126,.7152,.0722))),c.rgb,{P("amount")});return c;"; break;
                    case ScriptNodeKind.ShaderContrast: body = $"vec4 c={color};c.rgb=(c.rgb-.5)*{P("amount")}+.5;return c;"; break;
                    case ScriptNodeKind.ShaderBrightness: body = $"vec4 c={color};c.rgb*={P("amount")};return c;"; break;
                    case ScriptNodeKind.ShaderInvert: body = $"vec4 c={color};return vec4(1.0-c.rgb,c.a);"; break;
                    case ScriptNodeKind.ShaderGrayscale: body = $"vec4 c={color};return vec4(vec3(dot(c.rgb,vec3(.2126,.7152,.0722))),c.a);"; break;
                    case ScriptNodeKind.ShaderPosterize: body = $"vec4 c={color};c.rgb=floor(c.rgb*({P("levels")}-1.0)+.5)/({P("levels")}-1.0);return c;"; break;
                    case ScriptNodeKind.ShaderGaussianBlur: case ScriptNodeKind.ShaderBoxBlur: case ScriptNodeKind.ShaderBloom:
                        int q = Math.Clamp((int)ShaderLibrary.Value(node, "quality"), 1, 4); loopSamples = (2 * q + 1) * (2 * q + 1); samples += loopSamples;
                        string weight = node.Kind == ScriptNodeKind.ShaderBoxBlur ? "1.0" : "exp(-dot(d,d)/" + N(q * q * .5) + ")";
                        body = $"vec4 total=vec4(0);float weights=0.0;for(int x=-{q};x<={q};x++)for(int y=-{q};y<={q};y++){{vec2 d=vec2(x,y);float w={weight};vec4 c={I("Color", "uv+d*" + P("amount") + "/kneFrame.yz")};";
                        if (node.Kind == ScriptNodeKind.ShaderBloom) body += $"c.rgb=max(c.rgb-vec3({P("threshold")}),vec3(0));";
                        body += "total+=c*w;weights+=w;}vec4 c=total/weights;";
                        body += node.Kind == ScriptNodeKind.ShaderBloom ? $"return vec4(({color}).rgb+c.rgb,({color}).a);" : "return c;"; break;
                    case ScriptNodeKind.ShaderMotionBlur:
                        int count = Math.Clamp((int)ShaderLibrary.Value(node, "quality"), 2, 16); samples += count; loopSamples = count;
                        body = $"vec4 c=vec4(0);for(int i=0;i<{count};i++)c+={I("Color", "uv+(float(i)/" + N(count - 1) + "-.5)*vec2(" + P("x") + "," + P("y") + ")/kneFrame.yz")};return c/{N(count)};"; break;
                    case ScriptNodeKind.ShaderChromatic: body = $"vec4 c={color};c.r=({I("Color", "uv+vec2(" + P("amount") + "/kneFrame.y,0)")}).r;c.b=({I("Color", "uv-vec2(" + P("amount") + "/kneFrame.y,0)")}).b;return c;"; samples += 3; break;
                    case ScriptNodeKind.ShaderVignette: body = $"vec4 c={color};c.rgb*=1.0-clamp(dot(uv-.5,uv-.5)*2.0*{P("amount")},0.0,1.0);return c;"; break;
                    case ScriptNodeKind.ShaderScanlines: animated = true; body = $"vec4 c={color};c.rgb*=1.0-{P("amount")}*(.5+.5*sin((uv.y*kneFrame.z+kneFrame.x*8.0)*3.14159265));return c;"; break;
                    case ScriptNodeKind.ShaderLensDistortion: body = $"vec2 p=uv-.5;return {I("Color", "p*(1.0+" + P("amount") + "*dot(p,p))+.5")};"; break;
                    case ScriptNodeKind.ShaderPixelate: body = $"return {I("Color", "(floor(uv*kneFrame.yz/" + P("amount") + ")+.5)*" + P("amount") + "/kneFrame.yz")};"; break;
                    case ScriptNodeKind.ShaderSharpen: body = $"vec4 c={color};vec4 n=({I("Color", "uv+vec2(1,0)/kneFrame.yz")}+{I("Color", "uv-vec2(1,0)/kneFrame.yz")}+{I("Color", "uv+vec2(0,1)/kneFrame.yz")}+{I("Color", "uv-vec2(0,1)/kneFrame.yz")})*.25;return vec4(c.rgb+(c.rgb-n.rgb)*{P("amount")},c.a);"; samples += 5; break;
                    case ScriptNodeKind.ShaderSobel:
                        body = "float gx=0.0,gy=0.0;"; samples += 9;
                        for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                            body += "{float l=dot((" + I("Color", "uv+vec2(" + N(x) + "," + N(y) + ")/kneFrame.yz") + ").rgb,vec3(.2126,.7152,.0722));gx+=l*" + N(x * (y == 0 ? 2 : 1)) + ";gy+=l*" + N(y * (x == 0 ? 2 : 1)) + ";}";
                        body += $"return vec4(vec3(length(vec2(gx,gy))),({color}).a);"; break;
                    case ScriptNodeKind.ShaderDithering: body = $"vec4 c={color};vec2 p=mod(floor(uv*kneFrame.yz),4.0);float d=(mod(p.x*2.0+p.y*3.0,4.0)*4.0+mod(p.x+p.y*2.0,4.0)+.5)/16.0-.5;c.rgb=floor(c.rgb*({P("levels")}-1.0)+d+.5)/({P("levels")}-1.0);return c;"; break;
                    case ScriptNodeKind.ShaderMix: body = $"return mix({I("A")},{I("B")},clamp({I("T")},0.0,1.0));"; break;
                    case ScriptNodeKind.ShaderAdd: body = $"return {I("A")}+{I("B")};"; break;
                    case ScriptNodeKind.ShaderSubtract: body = $"return {I("A")}-{I("B")};"; break;
                    case ScriptNodeKind.ShaderMultiply: body = $"return {I("A")}*{I("B")};"; break;
                    case ScriptNodeKind.ShaderScreen: body = $"return 1.0-(1.0-{I("A")})*(1.0-{I("B")});"; break;
                    case ScriptNodeKind.ShaderStep: body = $"return step({I("Edge")},{I("Value")});"; break;
                    case ScriptNodeKind.ShaderSmoothstep: body = $"return smoothstep({I("Min")},max({I("Max")},{I("Min")}+vec4(.00001)),{I("Value")});"; break;
                    default: throw new InvalidDataException("Неизвестная операция шейдера.");
                }
                long cost = Regex.Matches(body, @"kne_sample\(").Count;
                foreach (Match call in Regex.Matches(body, @"kne_n\d+(?=\()")) cost += samplingCosts[call.Value];
                cost *= loopSamples;
                if (cost > 512) Fail(node, "больше 512 выборок текстуры на пиксель; уменьшите качество / вложенность размытия");
                samplingCosts[names[node.Id]] = cost;
                source.AppendLine("// block " + Convert.ToHexString(Encoding.UTF8.GetBytes(node.Id)));
                source.Append("vec4 ").Append(names[node.Id]).Append("(vec2 uv){").Append(body).AppendLine("}");
                active.Remove(node.Id); done.Add(node.Id); return names[node.Id];
            }
            // Validate disconnected drafts too: a cycle must not hide outside the output branch.
            foreach (var node in model.Nodes) Visit(node);
            return names[outputs[0].Id];
        }
        string final = CompileGraph(graph, sourcePath == null ? "shader" : Path.GetRelativePath(root, sourcePath).Replace('\\', '/'), 0);
        if (samples > 512 || source.Length > 256 * 1024) throw new InvalidDataException("Шейдер слишком сложный: уменьшите вложенность размытия / количество блоков.");
        source.Append("void main(){vec4 c=clamp(").Append(final).Append("(fsUv),0.0,1.0);outputColor=vec4(c.rgb*c.a,c.a)*fsColor;}");
        var targets = graph.Nodes.Where(node => node.Kind == ScriptNodeKind.ShaderTexture).Select(node => (node.Shader!.Target, node.Shader.Screen)).Distinct().ToArray();
        if (targets.Length > 1) throw new InvalidDataException("Один .kneshader применяется к одной цели. Используйте отдельный файл для другой цели.");
        var target = targets.FirstOrDefault();
        if (target.Target == "screen" && string.IsNullOrWhiteSpace(target.Screen)) throw new InvalidDataException("Укажите название экрана в блоке Texture.");
        return new(source.ToString(), target.Target ?? "scene", target.Screen ?? "", animated, parameters);
    }
    private static void Fail(ScriptNode node, string message) => throw new InvalidDataException($"Шейдер, блок «{node.Title}» ({node.Id}): {message}.");
    private const string Header = """
        #version 450
        layout(set=0,binding=0) uniform texture2D Image;
        layout(set=0,binding=1) uniform sampler ImageSampler;
        layout(set=0,binding=2) uniform KneGlobals { vec4 kneFrame; vec4 kneOrigin; };
        layout(location=0) in vec2 fsUv;
        layout(location=1) in vec4 fsColor;
        layout(location=0) out vec4 outputColor;
        vec4 kne_sample(vec2 uv){vec4 c=texture(sampler2D(Image,ImageSampler),uv);return vec4(c.a>0.00001?c.rgb/c.a:vec3(0),c.a);}
        vec2 kne_rotate(vec2 p,float a){float c=cos(a),s=sin(a);return vec2(c*p.x-s*p.y,s*p.x+c*p.y);}
        vec2 kne_hash(vec2 p){return fract(sin(vec2(dot(p,vec2(127.1,311.7)),dot(p,vec2(269.5,183.3))))*43758.5453);}
        float kne_simplex(vec2 p){
          const float F=.366025404,G=.211324865;
          vec2 i=floor(p+(p.x+p.y)*F),a=p-i+(i.x+i.y)*G;
          vec2 o=a.x>a.y?vec2(1,0):vec2(0,1),b=a-o+G,c=a-1.0+2.0*G;
          vec3 w=max(vec3(.5)-vec3(dot(a,a),dot(b,b),dot(c,c)),0.0);w*=w;w*=w;
          return 70.0*dot(w,vec3(dot(kne_hash(i)*2.0-1.0,a),dot(kne_hash(i+o)*2.0-1.0,b),dot(kne_hash(i+1.0)*2.0-1.0,c)));
        }
        float kne_voronoi(vec2 p){vec2 cell=floor(p),f=fract(p);float d=2.0;for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++){vec2 o=vec2(x,y);d=min(d,length(o+kne_hash(cell+o)-f));}return clamp(d,0.0,1.0);}
        """ + "\n";
}
