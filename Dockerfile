FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DesensitizeProxy.slnx ./
COPY src/DesensitizeProxy.Core/DesensitizeProxy.Core.csproj src/DesensitizeProxy.Core/
COPY src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj src/DesensitizeProxy.AspNetCore/
RUN dotnet restore src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj
COPY src ./src
RUN dotnet publish src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj -c Release -o /out --self-contained false

FROM runtime AS final
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8403
ENTRYPOINT ["dotnet", "DesensitizeProxy.AspNetCore.dll"]
