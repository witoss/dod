FROM node:24-alpine AS web
WORKDIR /web
COPY src/Dod.Web/package*.json ./
RUN npm ci
COPY src/Dod.Web/ ./
ARG APP_REVISION
RUN VITE_APP_REVISION="$APP_REVISION" npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Dod.Api/Dod.Api.csproj src/Dod.Api/
RUN dotnet restore src/Dod.Api/Dod.Api.csproj
COPY src/Dod.Api/ src/Dod.Api/
RUN dotnet publish src/Dod.Api/Dod.Api.csproj -c Release -o /app --no-restore
COPY --from=web /web/dist /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
RUN mkdir /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080 Storage__Path=/data/dod.db
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dod.Api.dll"]
