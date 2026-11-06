using Godot;

/// <summary>
/// 全局可调参数。所有数值集中在这里，方便对齐视频手感。
/// 左侧弹珠台坐标 = 屏幕像素（760×1080），右侧战场 = 1000×1000 像素。
/// </summary>
public sealed class GameSettings
{
    // ---- 左侧弹珠台：颜色球 ----
    public float BallStartValue = 16f;          // 开局每个颜色球的数值
    public int BallPerColor = 3;                // 每种颜色几个球（4色 × 3 = 12）
    // 半径（用户要求：前面和旧版一样，打久了也要继续变，但要慢）：n = log2(v / ref(t)) 是比参考值多几次翻倍，x = 6 + 0.95·n
    // 旧版范围内照旧 r = clamp(x, 6, 24)；超过 24 后按对数慢慢变大；比半径参考值轻 BallFlatSeconds/140 次翻倍以上才开始缩
    // （重置成 16 的球要打满 10 分钟才到这条线）：r = 6·2^(−m / BallShrinkDoublings)，m = 过线几次翻倍，一直缩但永远 > 0
    public float BallFlatSeconds = 600f;             // 前 10 分钟和旧版完全一样（小球、气流都是）
    public float BallShrinkDoublings = 48f;          // 过线后每 48 次翻倍半径减半（重置的球：30 分钟 ≈ 5.3 px，60 分钟 ≈ 4.4 px）
    public float BallSoftRadius = 24f;               // 旧版上限
    public float BallSoftRadiusScale = 6f;           // 超过 24 px 后 r = 24 + 6·ln(1 + (x − 24)/6)
    public float GrowthBallRadiusPerDecade = 2.2f;   // 数值每 ×10，半径 +2.2
    public float BallSizeDoublingSeconds = 115f;     // 气流（升力）参考值 16·2^(t/115)（视频实测；胜率模型也用它）
    public float BallRadiusDoublingSeconds = 140f;   // 半径参考值 16·2^(t/140)：同样数值的球随时间变小（视频是 115，用户要求变小慢一点）
    public float GrowthBallMaxSpeed = 550f;
    public float GrowthBallGravity = 180f;
    public float GrowthBallDrag = 0.002f;
    public float GrowthBallStuckThreshold = 4f;
    public float GrowthBallStuckDistance = 5f;

    // ---- 上升气流（中央空隙） ----
    // 数值达到该阈值后气流吹不动，球会掉下去。按个头算：开局正好 33K，之后随半径参考值一起翻倍
    // （视频里 240 秒时 131K 还悬在气流里，420 秒时 262K 仍被吹上去）
    public float WindMassThreshold = 33000f;
    public float WindLiftMax = 1500f;            // 最轻的球受到的向上加速度
    public float WindLiftMin = 620f;             // 快到阈值的球受到的向上加速度（仍大于重力）
    public float WindHeavyLift = 0.8f;           // 刚过阈值的球：升力 = 重力 × 0.8，只能减速慢慢沉下去
    public float WindHeavyFadeU = 0.3f;          // 再重 ×10 左右，升力降到 0（旧版）
    public float WindHeavyPull = 110f;           // 吹不动的球在杯组上方被吸向中间空隙的水平加速度
    public float WindHeavyPullTop = 150f;        // 吸力从这个高度往下生效（到杯口为止）
    public float WindJitter = 80f;
    public float WindLightGain = 0.5f;           // 比参考值轻过 BallFlatSeconds 那条线的球，升力再按旧直线斜率的一半慢慢变大（30 分钟 ≈ 1920，60 分钟 ≈ 2540）
    public float WindMaxRiseSpeed = 300f;        // 上升限速；升力超过 WindLiftMax 的球限速 × √(升力 / WindLiftMax)
    public float WindOutwardPush = 60f;          // 气流顶部把球往两侧推

    // ---- 旋转体（4 组，每组 1 中心 + 6 卫星） ----
    public float PegRadius = 5.5f;

    // ---- 底部槽 ----
    // 球落到这个高度以下、还在最外两条槽（风暴 / 狙击）里侧 SlotEdgeMargin 像素以外时，受一个往里的水平加速度（px/s²），
    // 少掉进风暴、狙击槽；0 = 关掉。SlotEdgePull 管风暴那边，SniperEdgePull 管狙击那边
    public float SlotEdgePull = 230f;
    // 用户嫌狙击少（2026-10-04），狙击那边拉得轻一点
    public float SniperEdgePull = 200f;
    public float SlotEdgePullTop = 800f;
    public float SlotEdgeMargin = 30f;
    // 球落到 SlotEdgePullTop 以下、在护盾槽（中间那条，两根柱子中心之间）上方时，受一个离开槽中心的水平加速度：
    // 槽中心为 0，往两边线性变大，到柱子中心是这么多（px/s²）。靠边的球被带过柱子，落进旁边的机枪 / 大球槽，
    // 护盾槽少进一点（用户要的）；0 = 关掉
    public float ShieldSlotPush = 300f;
    // 推力往下渐弱：到 ShieldSlotPushEndY - ShieldSlotPushFade 还是全力，再往下线性减到 ShieldSlotPushEndY 为 0。
    // 柱子顶在 y≈935，快落进护盾槽的球（离槽口几十像素以内）不再被吹出去（用户要的）
    public float ShieldSlotPushEndY = 895f;
    public float ShieldSlotPushFade = 35f;

    // ---- 底部槽：设置界面里改了槽概率时加的固定拉力场 ----
    // 用户要静态的（2026-10-05：「静态拉力就行，不要动态的」）：力只看球在哪，每颗球都一样，开局时按各槽的份额
    // （SlotShare*，按比例算，不用加起来正好 100）算好，之后不变。上面三个力照样有，这个场叠在上面。
    // SlotPullTop 到 SlotPullBottom 之间，以中间三个槽（机枪、护盾、大球）的中心为界分成 4 段，每段一个水平加速度，最多 SlotPullMax；
    // 顺着力的方向最多把球推到 |力| / SlotPullDrag 那么快（不刹车），往中间收的场才不会把球甩过头。
    // 4 个力开局时用一个一维模型反推（LeftArena.BuildSlotPull）：柱子一局里会往外挪，所以取一局里 8 个时刻（柱子按那时的位置），
    // 一堆虚拟球从「自然落点」（按默认份额在各槽里均匀）出发，顺着拉力场（左右各 SlotPullModelSmooth 像素平均过，球一路会左右晃）
    // 漂 SlotPullModelK 那么久，几个时刻平均，调 4 个力直到落点的份额 = 设定。
    // 模型的两个参数按实测拟合（2026-10-05，6 局：0/50/0/50/0 和 30/10/10/20/30 各两局、20×5、0/0/100/0/0）。份额是默认值时力全是 0，等于没有；比例是大约的，越极端越不准
    // 默认值 ≈ 物理自然落槽的比例（2026-10-04 实测 12.6/23.2/32.7/19.8/11.7 等几次的平均）
    public float SlotShareStorm = 10f, SlotShareMg = 22f, SlotShareShield = 32f, SlotShareBall = 23f, SlotShareSniper = 13f;
    public float SlotPullTop = 580f;             // 中间气流口下面一点（再往下不会进杯）
    public float SlotPullBottom = 930f;          // 柱子顶（y ≈ 928–936），再往下已经在两根柱子中间了
    public float SlotPullModelK = 0.5f;          // 模型里虚拟球漂多久：位移 ≈ 这个 × 力（px/s²）
    public float SlotPullModelSmooth = 105f;     // 模型里力按 ±这么多像素平均
    public string SlotPullForce = "";            // 调试：「a0,a1,a2,a3」直接用这 4 个力（px/s²），不反推
    public float SlotPullDrag = 3f;              // 拉力最多把球推到 |力| / 这个 那么快（顺着拉力方向，越接近越推得轻，不刹车）：
                                                 // 没有它往中间收的力会把球越推越快、甩过头（重力只有 180），力是 0 的地方不影响球
    public int SlotPullDragMode = 1;             // 调试：0 = 线性阻尼（力 - Drag × vx，会把球横向的速度都刹掉）
    public string SlotPullBase = "";             // 调试：模型起点（各槽的自然份额「s0,…,s4」），空 = 用默认份额
    public float SlotPullMax = 700f;             // 每段最多这么多 px/s²
    public float SlotPullBlend = 15f;            // 两段交界（槽中心）左右各这么多像素里线性过渡

    /// <summary>第 k 个槽（0 风暴 1 机枪 2 护盾 3 大球 4 狙击）的份额（没归一化）。</summary>
    public float SlotShare(int k) => k switch
    {
        0 => SlotShareStorm, 1 => SlotShareMg, 2 => SlotShareShield, 3 => SlotShareBall, _ => SlotShareSniper,
    };

    public void SetSlotShare(int k, float v)
    {
        switch (k)
        {
            case 0: SlotShareStorm = v; break;
            case 1: SlotShareMg = v; break;
            case 2: SlotShareShield = v; break;
            case 3: SlotShareBall = v; break;
            default: SlotShareSniper = v; break;
        }
    }

    // ---- 出生点 ----
    public float SpawnY = 30f;

    // ---- 右侧战场：开局（视频实测）----
    // 0 秒每队狙击火力 +1M（苍蝇乱飞后收拢成一束射向场地正中间，四束在中间对撞），
    // 1.8 秒机枪火力 +500K，4.88 秒护盾 +25M
    public float OpeningSniper = 1_000_000f;
    public float OpeningSniperLead = 0.21f;      // 视频第一帧时开局那束已经打出来约 0.21 秒：苍蝇已经散开在半径 50~150、没有炸开的放射拖尾，中心光盘边已在半径 29
    public float OpeningMgTime = 1.8f;
    public float OpeningShieldTime = 4.88f;
    public float StartMachineGunFire = 500_000f;
    public float StartShield = 25_000_000f;

    // ---- 资金模式的交易选项（游戏设置里开关，默认都关） ----
    // FundsUseProfit：现金不够时，可以从赚钱的持仓里借出浮盈来开新仓（被借的那笔投入减少，强平价跟着上移）
    // FundsMerge：同一阵营、同一方向、同一杠杆的新单并进原来那笔，开仓价按股数加权成均价
    public bool FundsUseProfit = false;
    public bool FundsMerge = false;

    // ---- 右侧战场：机枪 ----
    // 视频实测：机枪读数每帧正好少 0.03%（695.5K → −209，34.25M → −10.27K，460.4M → −138.1K），火力按 1.8%/秒 往下掉。
    // 每帧打几颗（视频里逐帧数弹头：火力 50 万 → 1 颗、6500 万 → ≈2 颗、2 亿 → ≈3 颗，高火力也不是几百颗连成一片）：
    // n = (这一帧的 0.03% ÷ MgBulletValue)^MgShotsExponent，随机取整，最多 MgMaxShotsPerStep 颗。
    // 每颗 = 这一帧的 0.03% 平分，但不超过 MgBulletCapAt1M × (火力 ÷ 100 万)^MgBulletCapExponent：
    // 火力 100 万以内基本碰不到上限（开局每发 150~250 点，「每发子弹对应 100 个像素」，铺地盘的速度就是这样量出来的）；
    // 视频 2 亿火力的弹流打进敌方领土是一根根 12 像素宽、100~250 像素长的「手指」，一颗只有 1~3 千点，
    // 所以火力很高时真正打出去的只是读数减少的一小部分（2 亿时约 1/8），不然一秒就能把整张图涂满。
    // 这个上限只管涂色：打到敌方护盾上按这一步读数减少的那份扣（每颗 × 总数 ÷ 打出去的，RightArena Bullet.Dmg）——
    // 视频 1140~1146 秒满场红色只剩黄方一个泡：红方机枪 6.2 亿每秒少 ≈10.5M，黄方护盾每秒掉 ≈9~10M；
    // 848~868 秒红方 4 亿每秒少 ≈7M，另外三家护盾一共每秒掉 ≈4.3M（场上别家地盘多，一部分花在涂色上）。
    // 打大球（扣敌方的、喂己方的、推球的质量）、子弹互撞也按这一份算（按要求），只有涂色受上限管
    // 场上子弹多于 MaxBullets 的一半以后按剩下的空位少打，满了就不打（读数照样按 0.03%/帧 往下掉）
    public float MgShotFraction = 0.0003f;
    public float MgBulletValue = 150f;
    // 按要求高火力打得更密（原来按视频 0.18 / 4：2 亿时每帧 ≈ 3 颗）：0.24 → 2 亿时 ≈ 4.2 颗、10 亿 ≈ 6 颗；
    // 每颗的上限不变（「手指」长短照旧），所以高火力真正打出去的也多了约 1.4 倍
    public float MgShotsExponent = 0.24f;
    public int MgMaxShotsPerStep = 8;
    public float MgBulletCapAt1M = 260f;
    public float MgBulletCapExponent = 0.43f;
    public int MaxBullets = 6000;
    public float BulletSpeed = 240f;             // 视频实测机枪曳光弹 ≈ 4 px/帧
    public float TurretRotationSpeed = Mathf.Pi / 5f;   // 顺时针 36°/s（视频实测：开局炮口比「朝场地正中间」逆时针偏 45°，约 1.25 秒转到正中间）
    // 弹道随机：视频低火力（每帧一颗）量弹流前沿：10%→90% 宽 3°（距离 100）/ 4.8°（160）/ 5.3°（200），
    // 正好是速度 ±10% 均匀分布拉开的角度（弹飞得越慢、落后炮口越多），方向本身几乎不散（σ < 1°）
    public float MgSpreadDeg = 2f;               // 每颗子弹方向在炮口朝向 ±2° 内随机（三角分布）
    public float MgSpeedJitter = 0.10f;          // 每颗子弹速度 ×(1 ± 0.10) 随机（均匀分布）
    public float PointDefenseRange = 300f;       // 大球 / 狙击 / 风暴子弹
    public float PointDefenseNormalRange = 150f; // 普通机枪子弹
    public float PointDefenseFireMultiplier = 3f; // 点防时每步打 3 颗
    public float PointDefenseSpeedMultiplier = 2f;
    public float BulletPaintRadius = 5.6f;       // 子弹涂色圆盘半径（视频里一颗子弹犁出来的线约 11 像素宽）
    public float BulletTrailSeconds = 0.3f;      // 曳光拖尾长度 = 速度 × 该时间（视频里约 70 像素）

    // ---- 右侧战场：涂色 / 占中立地（视频实测）----
    // 子弹不会过期：一直飞、碰边反弹，直到数值在中立/敌方领土上涂完（敌方像素 1 点 1 个、中立像素 1.5 点 1 个，
    // 高火力的子弹在敌方领土上犁出一根根几百像素长的「手指」），或者打在护盾、大球、敌方子弹上。
    // 中立像素 1.5 点 1 个：视频 10~22 秒红方机枪每秒打出约 1.36 万点，地盘涨 0.69 万像素/秒，
    // 其中约四分之一的子弹打在别的阵营身上；一颗子弹在空地上只涂出差不多一个涂色圆盘，地盘边缘是一圈圆鼓包。
    // 铺地盘那段机枪火力一直涨（约 20 秒时到 80~90 万），铺满（22~25 秒）以后只剩粒子收入，火力才慢慢往下掉：
    // 每占一个中立像素，阵营机枪火力 +NeutralHarvestMg、充能 +NeutralHarvestCharge（子弹、大球都算）。
    // 视频红方 5~22 秒：火力净涨 + 打出去的 − 粒子收入 ≈ 每个新像素 2.6~3.0；60 秒时充能 11.3~11.8 万
    public float NeutralPaintCost = 1.5f;
    public float NeutralHarvestMg = 2.7f;
    public float NeutralHarvestCharge = 0.32f;
    public float HarvestTau = 0.2f;              // 收成过两级缓冲再到账（每级 τ），读数平滑地涨

    // ---- 右侧战场：狙击 / 风暴 ----
    // 狙击（视频实测）：一群小亮点先从中心炸开（0.2 秒内散到 50~130 像素外），然后慢悠悠地（60~100 px/s）
    // 在中心周围像苍蝇一样乱飞，身后拖一条淡淡的灰白细弧线（不涂色）；约 1.3 秒激光头出膛，
    // 苍蝇一下子加速，绕着中心转起圈（顺时针逆时针各一半），这时才开始边飞边涂 1~3 像素宽的细线
    // （满屏鲜色的弧线，像一朵花，大多在 200 像素以内）；激光头飞出去以后，谁的飞行方向对准了自己在队形里的位置
    // 谁就直线追过去（2.0~2.4 秒陆续追出去）—— 一大把细线从花里收拢到激光头后面，像一把扫帚。
    // 队形沿当时的机枪方向（开局那束射向场地正中间）以 ≈140 px/s 慢慢飞出去、碰边反弹；
    // 归队以后涂的才是过曝的亮色：几十条细线并排拼成一条二三十像素宽的亮带（里面有细黑缝），好几秒才慢慢褪掉；
    // 弹头一小团白光，亮带中间一条霓虹色的路径线（红方是偏紫的粉红），归队得差不多了才亮起来。
    // 打护盾是穿甲的：打掉多少扣多少，剩下的继续飞。但要先撞上（按要求）：狙击子弹撞的是画出来的护盾环，
    // 激光头的白光和路径线停在环上不往里钻；打破护盾以后整束在环上顶住 SniperShieldBreakHold 秒、护盾环碎掉淡出，然后才穿过去。
    // 两束敌对激光相撞：子弹一颗一颗地对消（不弹开），头上炸一团白光；漏过去的零星子弹接着飞。
    public int SniperBulletsMax = 90;            // 开局那束 90 只苍蝇（视频数得出约 50~70 只，但细线总长是我们 60 只时的 1.3~1.9 倍）
    public float SniperSpeed = 140f;             // 开局那束：3.8 → 4.9 秒激光头沿对角线走了 153 像素，5.0 秒在正中间相撞
    public float SniperSwarmTime = 1.3f;         // 按 140 px/s 倒推，激光头 1.3 秒离开中心（苍蝇也正好这时开始加速、涂色）
    public float SniperCommitDelay = 0.5f;       // 出膛后至少转这么久才有苍蝇追出去（视频 1.8 秒以前花里的苍蝇都还在打转）
    public float SniperConvergeTime = 0.9f;      // 出膛后最多再转这么久，就一定掉头去追队形（视频 2.3 秒开始归队，3.2 秒差不多全归队）
    public float SniperLife = 20f;               // 队形最多维持这么久；之后子弹散开各飞各的，直到被消耗
    public float SniperShieldBreakHold = 0.4f;   // 狙击打破护盾以后先在护盾环上顶住这么久（环碎掉淡出），再穿过去（按要求，视频里没量过）
    public float SniperBurstMin = 400f;          // 炸开的初速，约 0.12 秒里减到乱飞的速度
    public float SniperBurstMax = 1000f;
    public float SniperBurstTau = 0.12f;
    public float SniperCruiseMin = 50f;          // 出膛前乱飞的速度（视频 0.5~1.3 秒亮点中位 60~70 px/s）
    public float SniperCruiseMax = 110f;
    public float SniperWander = 3f;              // 乱飞时转弯角速度随机游走：标准差（rad/s）、相关时间（s）—— 淡拖尾绕成一个个小圈
    public float SniperWanderTau = 0.4f;
    // 苍蝇都朝同一个方向绕中心转（用户要求）：1 = 屏幕顺时针（和视频粒子绕行、机枪转向一致），−1 = 逆时针，0 = 各转各的（旧）。
    // 出膛前像漩涡：往「这个方向的切线」拐（转弯角速度 = SniperSwirlSteer × 夹角），离中心比 SniperLeashRadius 远就往里偏、
    // 近就往外偏（再远/近 SniperLeashWidth 偏满 SniperSwirlTilt 弧度），上面再叠加随机游走（均值 方向·SniperSwirlSpin）。
    // 出膛后都朝这个方向按固定半径打圈。模型（_frames/s8/flymodel2.py）：出膛时 98% 绕中心同向转，细线 c ≈ −0.6（视频 1.5 秒 −0.6，
    // 方向不统一的旧版 ≈ 0）。只给随机游走加同向偏置不行：小圈同向，但整群贴边反着漂（同向绕行只有 9%~54%）
    public float SniperSwirlDir = 1f;
    public float SniperSwirlSteer = 4f;
    public float SniperSwirlTilt = 0.6f;
    public float SniperSwirlSpin = 1f;
    public float SniperLeashRadius = 70f;        // 乱飞时离中心超过这么远就往回拐，再远 SniperLeashWidth 拐得最急（rad/s）：都在 150 像素以内（SniperSwirlDir = 0 时）
    public float SniperLeashWidth = 60f;
    public float SniperLeashTurn = 4f;
    public float SniperFlingMin = 150f;          // 出膛后苍蝇加速到 Min + (Max − Min)·q²（视频 1.3~1.8 秒亮点 10/50/90% = 70/250/580 px/s）
    public float SniperFlingMax = 800f;
    public float SniperFlingTau = 0.25f;
    // 出膛后按固定半径转弯（半径 = Min + (Max − Min)·q^Pow，方向 = SniperSwirlDir；为 0 时按当时绕中心转的方向），不受中心拉：
    // 视频 1.3~1.8 秒曲率半径 45~110 像素、曲率圆心离中心 60~90 像素，涂色都挤在中心外 60~120 像素一圈；
    // 转一圈里飞行方向扫过所有方向，对准自己的队形位置就直线追过去 —— 2.6 秒一大把直线收向激光头（扫帚）
    public float SniperTurnRadiusMin = 60f;
    public float SniperTurnRadiusMax = 200f;
    public float SniperTurnRadiusPow = 2f;
    public float SniperCommitCos = 0.766f;       // 飞行方向和「去队形位置的方向」夹角小于 40° 就直线追过去
    public float SniperCatchSpeed = 3.5f;        // 追队形时最快 3.5 倍激光速度（每只 ±30%）：视频激光头走到 150~260 像素时苍蝇陆续归队
    public float SniperCatchJitter = 0.3f;
    public float SniperCatchGain = 6f;           // 追队形时速度 = 激光速度 + 位置误差 × 6
    // 还没归队的苍蝇（花、扫帚）涂的细线：大多 1 像素宽。视频开局 1.5 / 2.0 秒压缩以后平均线宽 1.5 / 2.6 像素
    // （flyw.py：队色面积 ÷ 骨架长度，2.0 秒以后好几条挤在一起才变宽），骨架上的亮度中位只有 68 / 85 —— 细线压缩以后发暗
    public float SniperFlyPaintRadius = 0.5f;
    public float SniperFlyThickSpread = 1f;      // 每只苍蝇的线宽 ×(1 + spread·q²)，q 每只随机
    // 这些细线按「已经涂了几秒」上色：不带新涂的过曝亮边，比鲜色稍暗。视频 1.5 秒压缩以后线心 R 只比底色高 55，
    // 整条过曝的细线（flyprof.py，同样压缩）高 106，看着又亮又粗
    public float SniperFlyPaintAge = 4f;
    public float SniperBeamLineRadius = 0.8f;    // 归队以后涂的细线（同一个 q，×(1 + 1.5q²)）：几十条并排拼成亮带
    public float SniperBeamLineSpread = 1.5f;
    public float SniperGhostSeconds = 0.5f;      // 出膛前苍蝇身后那条淡灰白细弧线有多长（秒）
    public float SniperClashDist = 24f;          // 两个激光头挨这么近就炸一团光
    public int SniperLanes = 20;                 // 队形横向最多几条细线（每条 3 只苍蝇；视频开局那束 15~30 条）
    public float SniperBeamWidth = 26f;          // 队形总宽（视频开局 1M 那束 25~35 像素）；数值每 ×10 宽 50%
    public float SniperWobble = 1.5f;            // 每条细线横向摆动的幅度（像素）：三个随机正弦叠加，相邻的线互相缠绕，亮带里留些细黑缝
    public float SniperFlashSeconds = 6f;        // 归队以后涂的细线、扫过的自家领土过曝 6 秒：饱和的亮队色慢慢褪，最后退回鲜色
                                                 // （视频实测红方亮带 0.5 秒 (245,25,80)、3.4 秒 (238,20,73)、3.9 秒 (231,23,64)）
    public float SniperLineJoin = 0.75f;         // 路径线：归队的子弹超过这个比例才开始亮，全归队最亮（视频里 d = 240 → 340 像素慢慢亮起来）
    public float SniperLineLag = 0.2f;           // 路径线亮度跟着归队比例走，但慢半拍（一阶滞后，秒）：开局那束 3.0 秒全归队，线在 d = 240 → 340 像素慢慢亮起来
    public float SniperLineDecay = 8f;           // 路径线每一段按 e^(−经过时间/8 s) 慢慢变淡（视频相撞 1.5 秒后还剩六七成）
    public float SniperLineFade = 1.5f;          // 激光结束后路径细线淡出的时间
    public int StormBulletsMax = 360;            // 视频实测 17M 风暴每圈 360 颗（角间距 1.000°），远处涂出一条条带缝的放射线
    public float StormSpeedMultiplier = 0.805f;  // 视频实测两次风暴（17M、34M）环扩张都是 193 px/s
    public int StormPulses = 3;                  // 视频里一条风暴飘字打出三圈，间隔约 5.1 秒
    public float StormPulseInterval = 5.1f;
    // 每圈带的火力 = 风暴火力 × 这个比例（1/StormPulses 就是三圈平分）。视频实测每圈带的比三圈平分多：
    // 17M 风暴相邻两队每圈掉 G 0.79/0.99/1.11M、B 0.98/1.36/1.37M，34M 风暴 R 1.52/2.21/2.50M、Y 2.22/2.55/2.66M；
    // 三圈平分的话，就算一路不涂色、不被拦，能打到护盾上的也只有 G 0.87M、B 0.90M、R 1.93M、Y 1.96M（射线追踪，含一次墙面反弹）。
    // 每圈带整份又太多：机枪、护盾按视频对齐的同一测试里，1 时 G、B 掉 6.7M、7.3M，0.55 时掉 3.06M、3.67M（视频 2.90M、3.72M）
    public float StormPulseValueFraction = 0.55f;
    public float StormFlashSeconds = 4f;         // 风暴子弹出膛后这段时间里扫过自家领土会过曝（拼成亮圆盘）
    // 风暴子弹的寿命（秒，每颗在 ±StormLifeJitter 内随机），到时散掉；0 = 不散，一直反弹到被消耗完。
    // 视频实测（数环和各墙面镜像环上的白点）：出膛 4.6 秒前一颗不少，之后一点点散，6.1 秒散光。
    // 相邻两队吃到直射、一次反弹和贴角的二次反弹（3.3～5.3 秒）；对角那队直射 4.85 秒才到、反弹要 5.9 秒以后，
    // 视频里它几乎不掉盾（17M 风暴下黄队只掉约 0.07M，同样的测试里我们掉 0.31M，不散时掉 1.9M）。
    // 不散的话没打中的子弹满场反弹，最后全砸到各家护盾和点防上
    public float StormBulletLife = 5.4f;
    public float StormLifeJitter = 0.7f;

    // ---- 右侧战场：粒子 ----
    // 视频里粒子是密密麻麻的灰白小点，以约 10 px/s 往中心漂，寿命到了就淡出 —— 寿命到了也算被中心吸收
    // （领土越大收入越多），落到敌方领土上的粒子直接消失。
    // 视频实测 60 秒时红方约 27% 领土：机枪收入 ≈ 12.4K/秒、充能 ≈ 1.2K/秒 → 每像素每秒 ≈ 0.05，九成进机枪
    // （机枪按 1.8%/秒 打出去，收支平衡在 70~90 万，和视频一致）。收入 ∝ 出生率 × 每颗价值：两个一起改，乘积保持 ≈ 0.0504。
    // 疏密（视频 60 秒，G/B/Y 领土上连续 13 帧都连得上的在漂粒子）：约 18～21 个/千像素；占得久的老领土（底色暗）里密，
    // 刚占的（底色亮）稀，中心附近最密 —— 粒子要活得久（10～20 秒）一路往里漂一路攒，才有这么密、这么里密外疏。
    // 原来寿命 4～7 秒、出生率 0.012：平均少约 20%，里外差得也不够
    public float ParticleSpawnPerPixel = 0.007f; // 每个己方像素每秒产出粒子的期望数
    public float ParticleValue = 7.2f;
    public float ParticleMgShare = 0.91f;
    // 充能那一份再乘这个倍数（不从机枪里扣）。视频 60～600 秒中立地铺满以后充能每秒涨 1.5～3.4K，
    // 原来只有 0.7～1K（600 秒时视频各队已经 1～2 颗额外弹珠，我们才 0～1 颗）
    public float ParticleChargeBoost = 2.6f;
    public float ParticleLifeMin = 10f;
    public float ParticleLifeMax = 20f;
    // 每颗粒子的亮度倍率 = Min + Span·u^1.5（u 均匀分布，出生时定）。视频实测在漂粒子的亮度：中位约为原来（0.45 + 1.25·u^1.5）的
    // 0.84 倍，暗的那头更暗（10% 分位 ≈ 中位的 0.45 倍，原来 0.55 倍）
    public float ParticleBrightMin = 0.3f;
    public float ParticleBrightSpan = 1.05f;
    // v(d) = base + K / (d + d0)。视频实测（逐个跟踪粒子）：离中心 160～415 px 中位速度都在 9～10 px/s，往里略快
    public float ParticleSpeedBase = 9f;
    public float ParticleSpeedK = 150f;
    public float ParticleSpeedD0 = 20f;
    // 向心速度偏转角：正 = 屏幕上逆时针绕行，负 = 顺时针。视频实测各队都是顺时针约 12°（中位 8～17°）
    public float ParticleSwirlDeg = -12f;
    // 每颗粒子出生时定下自己的速度倍率 1 ± ParticleSpeedJitter 和偏角 ± ParticleSwirlJitterDeg（三角分布），一辈子不变。
    // 视频实测同一处的粒子速度 σ ≈ 21%、方向 σ ≈ 7°，各走各的，点阵一直在变；全都一样的话整片粒子像一张图在平移。
    // 再按相邻粒子散开的快慢细调：视频比 0.5 / 17° 时的我们慢 13～15%
    public float ParticleSpeedJitter = 0.43f;
    public float ParticleSwirlJitterDeg = 14.5f;
    // 拖尾：沿速度反方向 速度×ParticleTrailSeconds 像素（四舍五入，最多 ParticleTrailMax）。
    // 视频里背景的粒子差不多就是一个小点（每颗的光量约为原来 2 px 拖尾的 0.8 倍）；被大球吸的粒子跑得快，拖尾长一点
    public float ParticleTrailSeconds = 0.1f;
    public int ParticleTrailMax = 4;
    // 新涂的领土先少出粒子（视频：大球走过的路上 2～3 秒内几乎没有粒子，8～10 秒才恢复）：出粒子的概率在涂色后
    // ParticleFreshDelay 秒内是 ParticleFreshMin，之后按 1 − (1 − Min)·e^(−t/ParticleMatureTau) 涨到 1；
    // 过曝（风暴、狙击扫过）的像素也算刚涂的。Min = 1 = 不分新旧
    public float ParticleFreshMin = 1f;
    public float ParticleFreshDelay = 1.5f;
    public float ParticleMatureTau = 2.5f;
    // 大球附近的粒子（视频实测 3 个球 3.2M / 18M / 82M，按离球面的距离分箱量世界坐标下的径向速度）：
    // 贴着球面 ≈ 80–95 px/s，30 px 外 ≈ 45，50 px 外 ≈ 28，约 100 px 外回到背景流；切向 ≈ 0（偏角 < 5°，没有公转）。
    // 大球的场 = base + K/(surf+d0) + BallPullNear·e^(−surf/BallPullLen)：远处和中心的场一样（抢粒子的范围、球长大的速度不变），近处加速
    public float BallPullNear = 70f;
    public float BallPullLen = 36f;
    // 大球吸粒子的范围（视频实测约 100 px 外回到背景流）：离球面 BallGravityInner 以内全归大球，
    // 往外到 BallGravityReach 按 smoothstep 渐弱到 0，再按两边场强之比软切换。
    // 原来在球面外 100 px 处硬切换：粒子的方向、速度、拖尾长短在那一圈上突变，看起来像一层圆形滤镜
    public float BallGravityInner = 50f;
    public float BallGravityReach = 125f;
    // 绕球公转（视频里没有，按要求加上的特效）：被大球吸的粒子径向照常往里掉（吸收快慢不变），同时绕球转，
    // 切向速度 = tan(角)·径向速度·(1 − 离球面距离/BallGravityReach)：贴着球面转得最快，引力圈边上不转
    // （整圈都按满角度转的话，粒子被甩到领土外的空地上铺成一层，像滤镜）；正 = 逆时针，0 = 直直地掉进去（视频原样）
    public float BallSwirlDeg = 70f;
    public float BallAbsorbDepth = 7f;           // 粒子越过球环再往里约 7 px 才被吸收（视频里粒子在 ≈ 0.93 倍半径处消失）
    // 大球每吸一颗粒子涨的数值（单独定，不跟 ParticleValue 走）。粒子改成活得久、出生少、每颗更值钱（7.2）以后，
    // 实测大球每秒吸进的粒子数和原来差不多（30/40/50 秒四队各发一个 30 万的球，新旧各跑两局：新 ≈ 1580 颗/球·秒，
    // 原来 ≈ 1550，单局噪声 ±10%）—— 扫过的地方粒子更密，但球停着时吸的是出生的量，出生率低了，两头抵消。
    // 所以还是 4.2/颗，大球长大的速度和原来一样；按 7.2 算会快 1.7 倍
    public float BallParticleValue = 4.2f;
    // 大球吸进的粒子（谁的粒子都算）也给球所属的阵营充能（用户要求，2026-10-04；视频没量）：每颗给的充能 = 中心吸收一颗粒子给的
    // （ParticleValue × (1 − ParticleMgShare) × ParticleChargeBoost ≈ 1.68）× 这个倍数；球照样按 BallParticleValue 长大，不扣。0 = 关掉
    public float BallParticleChargeShare = 1f;
    // 球芯里的闪烁亮点（视频：黑色球芯上每帧随机位置冒出的小亮点，只亮一帧，越靠外越密；数量随吸入的粒子多少变）：
    // 每吸进一个粒子，按这个概率在球芯里随机位置亮一帧
    public float BallCoreDotFraction = 1f;
    public int MaxParticles = 250_000;
    public float ChargeFull = 480_000f;          // 第 n 次充满阈值 = ChargeFull + ChargeStep·n
    public float ChargeStep = 240_000f;
    // 充能加成（视频第 7 集数值读数实测）：打破敌方护盾 → 打破的一方 +120K（857 / 888 / 1359.5 秒三次，别的队不加）；
    // 有阵营被淘汰 → 每个还活着的阵营都 +480K（864 / 889 / 1367 秒三次，击杀者不额外多拿；复活那次不算淘汰）。
    // 读数不是一下跳上去，而是 BonusChargeTime 秒里先快后慢涨完（1−(1−x)³ 拟合得很准）：护盾破后约 0.5 秒、
    // 阵亡后约 0.9 秒才开始涨；同一队的几笔排队一笔一笔涨（889 秒红队先涨完破盾的 +120K，再涨淘汰的 +480K）
    public float ShieldBreakCharge = 120_000f;
    public float EliminationCharge = 480_000f;
    public float BonusChargeTime = 2f;
    public float ShieldBreakChargeDelay = 0.5f;
    public float EliminationChargeDelay = 0.9f;

    // ---- 右侧战场：领土外观（视频实测的变暗曲线） ----
    public float LogoOpacity = 0.8f;             // 背景 logo 盖在领地颜色上的不透明度（0 = 关掉）
    public int LogoSize = 920;                   // logo 边长（像素，场地 1000）
    // 阵营覆灭冲击波：寿命（秒）、最大半径、波前推粒子的速度（px/s，随时间减弱）、推的带宽（波前两侧各这么多像素）
    public float DeathWaveLife = 1.8f;
    public float DeathWaveRadius = 750f;
    public float DeathWavePush = 420f;
    public float DeathWaveWidth = 45f;
    // 波前扫过子弹时，速度方向加上「往外的单位向量 × 这个值 × (1 − 进度)」再归一（快慢不变）：1 ≈ 开头最多偏 45°～90°
    public float DeathWaveBulletTurn = 1.2f;
    // 波前扫过大球时往外踢的速度（px/s，× (1 − 进度)，1 亿的球减半、更大的更少），最多到 BigBallSpeed 的这么多倍
    public float DeathWaveBallKick = 260f;
    public float DeathWaveBallMaxSpeed = 2f;
    public float ShieldFx = 1f;                  // 护盾特效（六边形网格、边缘辉光、外光晕）总强度，0 = 关掉
    public float ShieldFxMin = 0.04f;            // 1000 万以下护盾的特效强度（30 亿以上为 1）
    public float ShieldFxCurve = 1.6f;           // 强度 = Min + (1 − Min)·u^Curve，u 按对数从 1000 万到 30 亿：>1 时小护盾更淡、大护盾才亮
                                                 // （2500 万 ≈ 0.09，1 亿 ≈ 0.26，10 亿 ≈ 0.71，30 亿以上 1）
    public float ShieldHitFlash = 0.35f;         // 护盾被打中时闪亮的强度（护盾环、六边形网格、光晕一起乘），1 = 旧版；用户嫌亮，压低两次
    public float ShieldEdgeGain = 0.6f;          // 护盾边缘亮度（护盾环、外光晕、内侧辉光一起乘），1 = 旧版；用户嫌亮，再压低
    public float ShieldMaxRadius = 500f;         // 护盾半径上限 = 场地边长的一半（用户要求）
    public bool ShieldClash = true;              // 敌方护盾重叠时两边同时扣同样的数值，直到刚好不再接触
    public float TerritoryFadeHold = 1f;         // 新涂的像素保持最亮的时间
    public float TerritoryFadeTau = 18f;         // 之后 (e^(−(t−hold)/τ) − floor)/(1 − floor)
    public float TerritoryFadeFloor = 0.1787f;   // 约 32 秒后完全沉成暗色

    // ---- 右侧战场：大球 ----
    // 发射速度；之后匀速直线，只在相撞、被自家子弹推、被冲击波踢时改变速度。
    // 视频实测（10 fps 跟踪 1387 段）：大球速度 10/25/50/75/90% 分位 24/55/95/140/184 px/s，刚出现的球中位约 110，
    // 一条完整的刚发射轨迹在 105 左右走平（原来的 200 是最早随手定的，比视频快一倍，用户嫌快降到 110 / 200；
    // 之后用户又要动能稍微强一点，提到 125 / 230）
    public float BigBallSpeed = 125f;
    public float BigBallMaxSpeed = 230f;         // 不管怎么撞、怎么推、怎么被冲击波踢，速度都不超过这个 × 速度倍数（视频 90% 分位 184）
    // 被撞慢的球慢慢加速回去（按要求「大球不要一直卡在一个角落」）：以前没有最低速度，被别的大球迎面撞得只剩几 px/s 以后
    // 会在角落里慢慢漂上二三十秒。现在速度低于 BigBallSpeed × 速度倍数 × BigBallMinSpeed 时，沿当前方向每秒加 BigBallReaccel，
    // 加到这个速度为止（撞慢的那一下还看得到，几秒后恢复）；0 = 关掉
    public float BigBallMinSpeed = 0.4f;
    public float BigBallReaccel = 10f;
    // 高数值的大球跑得快一点（按要求，视频里没有）：发射速度、上限、被推 / 被踢的上限都 × (1 + Boost·u)，
    // u 按 log10(数值) 在 From..To 之间从 0 线性涨到 1：100 万以下不变，1000 万 +7%，1 亿 +13%，10 亿以上 +20%。
    // 数值变了速度按比例跟着变（吃粒子 / 吃自家子弹变大会加速，撞掉一截会稍微慢下来）
    public float BigBallSpeedBoost = 0.2f;
    public double BigBallSpeedBoostFrom = 1e6;
    public double BigBallSpeedBoostTo = 1e9;
    // 大球撞敌方大球 / 敌方护盾：双方各扣「较小者 × 0.8」（视频实测 23 次：撞护盾入射角 0~68°、撞球 2~85°，
    // 大小比 1~20 倍、数值 57K~2.5 亿，较小的一方都剩 18%~22%，和角度无关）
    public float BigBallClashLoss = 0.8f;
    // 大球撞护盾：两边差到这个倍数（100 倍）以上，小的一方整个扣光（双方各扣「较小者 × 1」），不再只扣 80%（按要求，视频里没有）
    public float BigBallShieldWipeRatio = 100f;
    public double BigBallShieldWipeMin = 1e8;    // 而且大的一方要到这个数（1 亿）以上才扣光（按要求从 10 亿改成 1 亿）
    // 自家子弹推大球：子弹被吸收时把它的动量加到球上（质量 ∝ 数值^BigBallMassExponent），乘这个倍数；0 = 不推。
    // 3000 万的球在 3 亿机枪的扇面里每秒吃 ~500 颗，约 0.2 秒就被推到上限速度；3 亿的球推得慢得多
    public float OwnBulletPush = 1f;
    public float OwnPushMaxSpeed = 1f;           // 被自家子弹推时最多到 BigBallSpeed × 速度倍数 的这么多倍（1 = 推不过发射速度）
    // 大球互撞是弹性碰撞（恢复系数 ≈ 1、切向不变），质量 ∝ 数值^0.58：视频实测 8 次，按撞前撞后的速度变化反推质量比，
    // 数值比 0.06~12 倍（8.1 万~2.5 亿）都符合，误差大多在 10% 以内（例：290 万撞 24 万，大球只让出 1/5 的速度）
    public float BigBallMassExponent = 0.58f;
    public float BigBallSpawnRadius = 32f;       // 阵营灭亡时弹出的大球先是这么大，约 0.3 秒长到应有的大小

    public static GameSettings CreateDefault() => new GameSettings();
}
