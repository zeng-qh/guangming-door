# 智慧光明开门网关 —— 多架构 Docker 镜像（amd64 + arm64）
# 树莓派 4B / 飞牛 fnOS 使用 arm64 架构镜像
#
# 大小优化：运行时用 chiseled-extra 精简镜像（~154MB），
#   比完整 Debian 版（~219MB）省约 30%，且自带时区数据与 ICU
# 内存优化：默认 workstation GC（.NET DATAS 自动收敛堆大小），适配 4GB 树莓派
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
    && mkdir -p /app/publish/data \
    && chown -R 1654:1654 /app

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app

# 时区固定北京时间，保证开门记录时间正确
# workstation GC + 关闭诊断端口，降低内存占用（4GB 树莓派友好）
# chiseled-extra 自带非 root 用户 app（UID 1654），默认即以此运行
ENV TZ=Asia/Shanghai \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build --chown=1654:1654 /app/publish .

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "GuangmingDoorApi.dll"]
