# Render has no native .NET runtime, so this is a Docker deploy. It also
# builds and bundles the frontend so one service serves both the API and
# the static site — see backend/Program.cs's UseStaticFiles/MapFallbackToFile.

# ---- frontend build ----
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- backend build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src
COPY backend/RagBook.Api.csproj ./backend/
RUN dotnet restore ./backend/RagBook.Api.csproj
COPY backend/ ./backend/
COPY --from=frontend-build /app/frontend/dist ./backend/wwwroot
RUN dotnet publish ./backend/RagBook.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=backend-build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
# Render sets $PORT and routes to it; Program.cs reads the same var and
# binds 0.0.0.0:$PORT, defaulting to 3001 if unset (e.g. local `docker run`).
EXPOSE 3001
ENTRYPOINT ["dotnet", "RagBook.Api.dll"]
