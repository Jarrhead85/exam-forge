# Многоступенчатая сборка: SDK для build, лёгкий рантайм для работы (ADR-001, §21).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только csproj+props: слой restore кэшируется между сборками.
# Restore по csproj веб-проекта: тянет всю цепочку зависимостей, без тестовых проектов решения.
COPY Directory.Packages.props ./
COPY src/ExamForge.Domain/ExamForge.Domain.csproj src/ExamForge.Domain/
COPY src/ExamForge.Application/ExamForge.Application.csproj src/ExamForge.Application/
COPY src/ExamForge.Infrastructure/ExamForge.Infrastructure.csproj src/ExamForge.Infrastructure/
COPY src/ExamForge.Web.Client/ExamForge.Web.Client.csproj src/ExamForge.Web.Client/
COPY src/ExamForge.Web/ExamForge.Web.csproj src/ExamForge.Web/
RUN dotnet restore src/ExamForge.Web/ExamForge.Web.csproj

COPY . .
# --no-restore НЕ использовать: без имплицитного restore publish не подтягивает
# microsoft.aspnetcore.app.internal.assets, и из вывода пропадает _framework/blazor.web.js
# (кабинет = пустая страница: circuit не может загрузить скрипт).
# ReadyToRun включается условием в ExamForge.Web.csproj при сборке под linux-x64
# (CLI-флаг нельзя: он протекает в WASM-клиент и падает NETSDK1095).
RUN dotnet publish src/ExamForge.Web/ExamForge.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Хранилище файлов и ключи DataProtection — тома (compose); культура для кириллицы.
ENV ASPNETCORE_URLS=http://+:8080 \
    EXAMFORGE_STORAGE_ROOT=/data/storage \
    DOTNET_system_globalization_invariant=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "ExamForge.Web.dll"]
