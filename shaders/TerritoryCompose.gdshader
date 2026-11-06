// 领土合成（GPU）：画在 1000×1000 的 SubViewport 里，一个像素对应一个领土像素。
//   field.rgb = CPU 叠好的加色层（护盾环、大球亮环、粒子、子弹，饱和加）
//   field.a   = 归属 + 新鲜度打包码：owner·63 + q（q = 0..62，带 4×4 有序抖动），255 = 中立
// 9×9 窗口里按阵营分别平均新鲜度 → 各自查色（内部柔和，阵营之间不混亮度）；
// 交界处按 3×3 帐篷权重把相邻阵营的颜色混进来（1 像素宽的抗锯齿边，不再是锯齿台阶）；
// 各阵营的领地盖上自己的 logo，大球球芯把底色压暗，最后加上加色层（粒子、子弹、护盾环在 logo 上面）。
shader_type canvas_item;
render_mode unshaded, blend_disabled;

uniform sampler2D field : filter_nearest;
uniform sampler2D lasers : filter_nearest;   // 狙击激光路径线（加色，RightArena.DrawLaserLines）：加在领土上、大球下面
uniform sampler2D lut : filter_nearest;      // 256 × 4：每个阵营一行，新鲜度 0..255 → 颜色
uniform vec3 neutral_col;
uniform int ball_count = 0;
uniform vec4 balls[64];                      // x, y, R², keep
uniform int blur_r = 4;
uniform float edge_aa = 1.0;                 // 0 = 硬边（调试对比用）
uniform sampler2D logo : filter_nearest;     // 背景 logo 图集（Logos.cs）：N×4N，阵营 t 在第 t 块
uniform float logo_opacity = 0.8;
// 护盾特效（视频里几十亿的护盾：球面上一层六边形能量网格，贴着边最亮、往里淡掉，外面一圈厚光晕）
uniform int shield_count = 0;
uniform vec4 shields[4];                     // x, y, R, 强度 0..1（护盾越高越强）
uniform vec4 shield_cols[4];                 // rgb, 被打中的闪光 0..1
uniform float shield_edge = 1.0;             // 边缘亮度（外光晕、内侧辉光；和 CPU 画的护盾环一起乘 ShieldEdgeGain）
uniform float time = 0.0;
uniform float hex_cell = 26.0;
// 阵营覆灭冲击波：x, y, 半径, 强度（随时间淡掉）；颜色 rgb
uniform int wave_count = 0;
uniform vec4 waves[4];
uniform vec4 wave_cols[4];               // 六边形格子宽（像素，球面中心处）

const float FieldSize = 1000.0;

vec3 lut_col(int owner, float sum, float n) {
	float v = sum / n * (255.0 / 62.0);
	float i0 = floor(v);
	vec3 a = texelFetch(lut, ivec2(int(i0), owner), 0).rgb;
	vec3 b = texelFetch(lut, ivec2(min(int(i0) + 1, 255), owner), 0).rgb;
	return mix(a, b, v - i0);
}

const vec2 HEX_S = vec2(1.0, 1.7320508);

// 到最近六边形边的距离（格子单位，格子宽 1）
float hex_edge(vec2 uv) {
	vec4 hc = floor(vec4(uv, uv - vec2(0.5, 1.0)) / HEX_S.xyxy) + 0.5;
	vec4 h = vec4(uv - hc.xy * HEX_S, uv - (hc.zw + 0.5) * HEX_S);
	vec2 q = abs(dot(h.xy, h.xy) < dot(h.zw, h.zw) ? h.xy : h.zw);
	return 0.5 - max(dot(q, HEX_S * 0.5), q.x);
}

// 护盾特效：球面六边形网格 + 内侧边缘辉光 + 外侧光晕，一道亮光沿着边慢慢转
vec3 shield_fx(vec2 pc) {
	vec3 fx = vec3(0.0);
	for (int i = 0; i < shield_count; i++) {
		vec2 d = pc - shields[i].xy;
		float r = length(d);
		float R = shields[i].z, s = shields[i].w;
		float hit = shield_cols[i].a;
		vec3 col = shield_cols[i].rgb;
		float glow = 4.0 + 12.0 * s;
		if (r > R + glow * 6.0) continue;
		float ang = atan(d.y, d.x);
		float sweep = pow(0.5 + 0.5 * cos(ang - time * 0.7 + float(i) * 1.7), 8.0);
		if (r >= R) {
			fx += col * exp(-(r - R) / glow) * (0.36 * s * shield_edge) * (1.0 + 0.6 * sweep + 1.5 * hit);
			continue;
		}
		// 球面投影：按球面弧长展开，越靠边格子越扁，像一层罩在球上的网
		float arc = asin(min(r / R, 1.0)) * R;
		vec2 dir = r > 1e-3 ? d / r : vec2(1.0, 0.0);
		float rot = time * 0.04 + float(i) * 0.9;
		dir = vec2(dir.x * cos(rot) - dir.y * sin(rot), dir.x * sin(rot) + dir.y * cos(rot));
		vec2 uv = dir * arc / hex_cell;
		float e = hex_edge(uv);
		float aa = max(fwidth(e), 1e-4);
		float line = 1.0 - smoothstep(0.045, 0.045 + 1.5 * aa, e);
		float depth = R - r;
		float rim = exp(-depth / (0.22 * R + 10.0));
		float hexI = line * rim * s * (0.55 + 0.80 * sweep + 2.0 * hit);
		float fill = exp(-depth / (0.06 * R + 4.0)) * (0.26 * s * shield_edge) * (1.0 + 1.5 * hit);
		fx += mix(col, vec3(1.0), 0.45) * (1.3 * hexI) + col * fill;   // 网格线偏白，压在同色领地上也看得出
	}
	return fx;
}

// 冲击波：一道很亮的白色波前 + 队色光晕，波前后面拖一片慢慢变淡的亮光，经过的领地被照亮一下
vec3 wave_fx(vec2 pc, vec3 terr) {
	vec3 fx = vec3(0.0);
	for (int i = 0; i < wave_count; i++) {
		float d = length(pc - waves[i].xy) - waves[i].z;     // >0 波前外面，<0 已经扫过
		float a = waves[i].w;
		vec3 col = wave_cols[i].rgb;
		if (d > 90.0 || d < -360.0) continue;
		float core = exp(-d * d / 50.0);
		float halo = d > 0.0 ? exp(-d / 22.0) : exp(d / 55.0);
		float wake = d < 0.0 ? exp(d / 140.0) * (1.0 - exp(d / 12.0)) : 0.0;
		fx += a * (vec3(1.0) * core * 1.1 + col * halo * 0.75 + col * wake * 0.28 + terr * (halo * 1.2 + wake * 0.7));
	}
	return fx;
}

int code_at(ivec2 q) {
	q = clamp(q, ivec2(0), ivec2(int(FieldSize) - 1));
	return int(texelFetch(field, q, 0).a * 255.0 + 0.5);
}

void fragment() {
	ivec2 p = ivec2(floor(UV * FieldSize));
	vec4 c = texelFetch(field, p, 0);
	int code = int(c.a * 255.0 + 0.5);
	int own = code == 255 ? 4 : code / 63;
	float sum[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);   // 各阵营新鲜度和（下标 4 = 中立，不用）
	float cnt[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);
	float w[5] = float[5](0.0, 0.0, 0.0, 0.0, 0.0);     // 3×3 帐篷权重（中心 4、边 2、角 1）
	for (int dy = -blur_r; dy <= blur_r; dy++)
	for (int dx = -blur_r; dx <= blur_r; dx++) {
		int k = code_at(p + ivec2(dx, dy));
		int o = k == 255 ? 4 : k / 63;
		if (o < 4) { sum[o] += float(k - o * 63); cnt[o] += 1.0; }
		if (abs(dx) <= 1 && abs(dy) <= 1) w[o] += float((2 - abs(dx)) * (2 - abs(dy)));
	}
	// 硬边：只留自己；抗锯齿：自己的权重按 edge_aa 在「只有自己」和「3×3 帐篷」之间插值
	for (int o = 0; o < 5; o++) w[o] = mix(o == own ? 16.0 : 0.0, w[o], edge_aa);
	vec3 terr = neutral_col * w[4];
	for (int o = 0; o < 4; o++)
		if (w[o] > 0.0) {
			// 每个阵营只在自己的领地里盖自己的 logo（交界处随抗锯齿权重一起混）
			vec4 lg = texelFetch(logo, p + ivec2(0, o * int(FieldSize)), 0);
			terr += w[o] * mix(lut_col(o, sum[o], cnt[o]), lg.rgb, lg.a * logo_opacity);
		}
	terr *= 1.0 / 16.0;
	vec2 pc = vec2(p) + 0.5;
	terr += shield_fx(pc);
	if (wave_count > 0) terr += wave_fx(pc, terr);
	// 狙击路径线在大球下面：球里（连同球边 1 像素的抗锯齿）挡掉
	float open = 1.0;
	for (int i = 0; i < ball_count; i++) {
		vec2 d = pc - balls[i].xy;
		float d2 = dot(d, d);
		if (d2 < balls[i].z) terr *= balls[i].w;
		open = min(open, clamp(length(d) - sqrt(balls[i].z) - 1.0, 0.0, 1.0));
	}
	vec3 lz = texelFetch(lasers, p, 0).rgb;
	COLOR = vec4(min(terr + lz * open + c.rgb, vec3(1.0)), 1.0);
}
