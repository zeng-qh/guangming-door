using System;
using GuangmingDoorApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// 一键开门精简版 + 通用设置：
//   - 账号密码在设置页填写并持久化到 data/settings.json；App 级凭据/请求头内置通用
//   - 登录后自动拉取 user_id 与门列表（rooms），门点参数全自动
//   - device_guid 按账号自动生成，服务器拒绝时可在设置页「高级」补填绑定设备 ID
//   - 只暴露「页面 + 状态 + 开门 + 记录 + 设置 + 测试」，不暴露转发/同步等调试端点
//   - 访问控制由发布链接自带的 sharecode 承担
var builder = WebApplication.CreateBuilder(args);

// 本地私有配置（git 忽略，不入镜像）：真实账号/设备/门点放这里，仓库与镜像只留占位
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.Configure<DoorApiOptions>(
    builder.Configuration.GetSection(DoorApiOptions.SectionName));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<OpenLogStore>();
builder.Services.AddSingleton<DoorGateway>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// 页面状态：只回门点名称与就绪标志，不暴露任何账号或凭据
app.MapGet("/api/status", (SettingsStore st, IOptions<DoorApiOptions> o) =>
{
    var v = o.Value;
    var name = v.Doors.Count > 0 ? v.Doors[0].Name : "";
    return Results.Ok(new { ok = true, doorName = name, ready = st.Configured });
});

// ---------- 设置 ----------
// 读取（脱敏，绝不返回密码明文）
app.MapGet("/api/settings", (SettingsStore st) => Results.Ok(st.Snapshot()));

// 保存：只需账号 + 密码；deviceGuid 可选（留空=自动生成 / 填了=手动绑定设备 ID）
app.MapPost("/api/settings", async (SettingsStore st, DoorGateway gw, HttpContext ctx) =>
{
    string? mobile = null, password = null, deviceGuid = null;
    try
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;
        if (root.TryGetProperty("mobile", out var m)) mobile = m.GetString();
        if (root.TryGetProperty("password", out var p)) password = p.GetString();
        if (root.TryGetProperty("deviceGuid", out var g) && g.ValueKind != System.Text.Json.JsonValueKind.Null)
            deviceGuid = g.GetString();
    }
    catch
    {
        return Results.BadRequest(new { ok = false, message = "请求体不是合法 JSON" });
    }

    var s = st.Save(mobile, password, deviceGuid);
    if (!s.Configured)
        return Results.BadRequest(new { ok = false, message = "账号和密码不能为空" });

    gw.InvalidateToken();   // 账号/设备变了，强制下次重新登录
    return Results.Ok(new { ok = true, message = "已保存", settings = st.Snapshot() });
});

// ---------- 测试 ----------
// openDoor=false（默认）：登录 → 设备校验 → 拉门列表，全程无副作用、不开门
// openDoor=true ：在上面通过后额外真实开门一次（会打开真实的门）
app.MapPost("/api/test", async (DoorGateway gw, SettingsStore st, OpenLogStore log, IOptions<DoorApiOptions> o, HttpContext ctx) =>
{
    var openDoor = false;
    try
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
        if (doc.RootElement.TryGetProperty("openDoor", out var od))
            openDoor = od.ValueKind == System.Text.Json.JsonValueKind.True;
    }
    catch { /* 空 body 视为仅测试连接 */ }

    if (!st.Configured)
        return Results.BadRequest(new { ok = false, message = "请先在设置页填写账号和密码" });

    var t = await gw.TestAsync(openDoor, st, ctx.RequestAborted);

    // 测试按钮里真实发出的开门也要留痕，与 /api/door/open 记录口径一致
    if (t.OpenResult != null)
    {
        var tr = t.OpenResult;
        log.Add(e =>
        {
            e.Ok = tr.Ok;
            e.Result = tr.Ok ? "成功" : "失败";
            e.Message = "测试开门 · " + (tr.Message ?? "");
            e.DoorId = tr.DoorId;
            e.DoorName = o.Value.Doors.Count > 0 ? o.Value.Doors[0].Name : null;
            e.HttpStatus = tr.HttpStatus;
            e.Relogged = tr.Relogged;
            e.ReloginAttempts = tr.ReloginAttempts;
            e.Sent = tr.Sent;
            e.Hint = tr.Hint;
        });
    }
    return Results.Ok(new
    {
        ok = t.Ok,
        steps = t.Steps,
        doorCount = t.DoorCount,
        deviceAccepted = t.DeviceAccepted,
        needDeviceGuid = t.NeedDeviceGuid,
        openedDoor = t.OpenedDoor,
        deviceGuidSource = t.DeviceGuidSource,
        hint = t.Hint
    });
});

// ---------- 记录 ----------
app.MapGet("/api/door/history", (OpenLogStore log, HttpContext ctx) =>
{
    var take = 30;
    if (int.TryParse(ctx.Request.Query["take"], out var t) && t > 0) take = t;
    var d = log.Query(take);
    return Results.Ok(new
    {
        ok = true,
        totalEver = d.TotalEver,
        successEver = d.SuccessEver,
        failEver = d.FailEver,
        retained = d.Retained,
        lastTime = d.LastTime,
        items = d.Items
    });
});

app.MapGet("/api/door/history/query", (OpenLogStore log, HttpContext ctx) =>
{
    var q = ctx.Request.Query;
    var page = int.TryParse(q["page"], out var p0) && p0 > 0 ? p0 : 1;
    var pageSize = int.TryParse(q["pageSize"], out var s0) && s0 > 0 ? s0 : 20;
    var d = log.QueryPaged(q["from"].ToString(), q["to"].ToString(),
                           q["result"].ToString(), q["keyword"].ToString(), page, pageSize);
    return Results.Ok(new
    {
        ok = true,
        totalEver = d.TotalEver,
        successEver = d.SuccessEver,
        failEver = d.FailEver,
        retained = d.Retained,
        totalMatched = d.TotalMatched,
        matchedSuccess = d.MatchedSuccess,
        matchedFail = d.MatchedFail,
        page = d.Page,
        pageSize = d.PageSize,
        pageCount = d.PageCount,
        items = d.Items
    });
});

// ---------- 一键开门 ----------
app.MapPost("/api/door/open", async (DoorGateway gw, OpenLogStore log, SettingsStore st, IOptions<DoorApiOptions> o, HttpContext ctx) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var doorName = o.Value.Doors.Count > 0 ? o.Value.Doors[0].Name : null;

    if (!st.Configured)
    {
        log.Add(e => { e.Ok = false; e.Result = "失败"; e.Message = "尚未配置账号密码"; e.DoorName = doorName; });
        return Results.Json(new { ok = false, message = "尚未配置账号密码，请先到「设置」页填写" }, statusCode: 400);
    }

    DoorResult r;
    try
    {
        r = await gw.OpenAsync(null, ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        sw.Stop();
        log.Add(e =>
        {
            e.Ok = false;
            e.Result = "失败";
            e.Message = "服务异常：" + ex.Message;
            e.DoorName = doorName;
            e.ElapsedMs = sw.ElapsedMilliseconds;
        });
        return Results.Json(new { ok = false, message = "服务异常：" + ex.Message }, statusCode: 500);
    }
    sw.Stop();

    log.Add(e =>
    {
        e.Ok = r.Ok;
        e.Result = r.Ok ? "成功" : "失败";
        e.Message = r.Message;
        e.DoorId = r.DoorId;
        e.DoorName = doorName;
        e.HttpStatus = r.HttpStatus;
        e.Relogged = r.Relogged;
        e.ReloginAttempts = r.ReloginAttempts;
        e.Sent = r.Sent;
        e.Hint = r.Hint;
        e.ElapsedMs = sw.ElapsedMilliseconds;
    });

    return Results.Json(new
    {
        ok = r.Ok,
        message = r.Message,
        doorId = r.DoorId,
        sent = r.Sent,
        relogged = r.Relogged,
        reloginAttempts = r.ReloginAttempts,
        hint = r.Hint
    }, statusCode: r.Ok ? 200 : (r.HttpStatus > 0 ? r.HttpStatus : 400));
});

app.MapFallbackToFile("index.html");

// 端口由发布平台注入的 --urls 决定
app.Run();
