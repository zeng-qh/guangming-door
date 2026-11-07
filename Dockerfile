# 智慧光明开门网关 —— 多架构 Docker 镜像（amd64 + arm64）
# 树莓派 4B / 飞牛 fnOS 使用 arm64 架构镜像
#
# 大小优化（最终形态）：
#   - 运行时用纯 chiseled 精简镜像（~118MB），不含 ICU/时区包
#   - ICU 用不变全球化模式替代（本应用无文化相关格式化需求，实测通过）
#   - 时区文件从构建阶段拷入，开门记录仍是北京时间
#   - 相比完整 Debian 版（219MB）总省约 45%
# 内存优化：workstation GC + DATAS 自适应堆，4GB 树莓派实测稳定态 ~26MB
#
# 构建阶段在构建机原生架构上交叉编译到目标架构；
# 运行阶段没有任何 RUN 指令，跨架构构建无需 QEMU 模拟目标架构。
# 注意：chiseled 镜像没有 shell，调试请用日志或临时换回完整镜像。

# ---------- 构建阶段 ----------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /app

COPY GuangmingDoorApi.csproj ./
RUN dotnet restore -a $TARGETARCH

COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app/publish --no-restore \
    && chown -R 1654:1654 /app

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app

# 时区固定北京时间（时区文件由下方 COPY 提供），保证开门记录时间正确
# 不变全球化 + workstation GC + 关闭诊断端口，内存与体积最优（4GB 树莓派友好）
# chiseled 自带非 root 用户 app（UID 1654），默认即以此运行
ENV TZ=Asia/Shanghai \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build --chown=1654:1654 /app/publish .

# 北京时区数据（纯 chiseled 不带 tzdata，从 SDK 构建阶段拷入单个时区文件）
COPY --from=build /usr/share/zoneinfo/Asia/Shanghai /usr/share/zoneinfo/Asia/Shanghai

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "GuangmingDoorApi.dll"]
