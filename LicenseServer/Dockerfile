# build 阶段的 SDK 镜像走 daocloud 的 MCR 镜像代理。
# 原因：本机直连 mcr.microsoft.com 被限速到约 85 KB/s，光拉 SDK 就要一个多小时；
# daocloud 提供的是同一批镜像，速度差两个数量级。
# 在能正常访问 MCR 的机器上部署时，把这一行改回 mcr.microsoft.com/dotnet/sdk:8.0 即可。
# 最终阶段的 aspnet 镜像保持直连不变（构建缓存里已有）。
FROM mcr.m.daocloud.io/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LicenseServer.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "LicenseServer.dll"]
