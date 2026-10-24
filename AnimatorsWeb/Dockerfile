# Используем официальный образ .NET SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем файлы проекта и восстанавливаем зависимости
COPY ["AnimatorsWeb.csproj", "./"]
RUN dotnet restore "AnimatorsWeb.csproj"

# Копируем всё остальное и собираем в Release
COPY . .
RUN dotnet publish "AnimatorsWeb.csproj" -c Release -o /app/publish

# Используем официальный образ ASP.NET Core для запуска
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Открываем порт 8080 (стандартный для ASP.NET Core в контейнерах)
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Запускаем приложение
ENTRYPOINT ["dotnet", "AnimatorsWeb.dll"]