SOLUTION = MendeleevProject.slnx
DEV_COMPOSE = deploy/docker-compose.dev.yml

.PHONY: db-up db-down run build test test-unit test-integration migration image

db-up:          ## Local PostgreSQL
	docker compose -f $(DEV_COMPOSE) up -d

db-down:
	docker compose -f $(DEV_COMPOSE) down

run: db-up      ## Service with the in-memory panel and the fake aggregator (appsettings.Development.json)
	dotnet run --project src/Mendeleev.Web

build:
	dotnet build $(SOLUTION)

test:           ## All tests; integration tests need Docker
	dotnet test $(SOLUTION)

test-unit:
	dotnet test tests/Mendeleev.UnitTests
	dotnet test tests/Mendeleev.ArchitectureTests

test-integration:
	dotnet test tests/Mendeleev.IntegrationTests

migration:      ## make migration NAME=AddSomething
	dotnet ef migrations add $(NAME) --project src/Mendeleev.Infrastructure --startup-project src/Mendeleev.Infrastructure --output-dir Database/Migrations

image:
	docker build -f src/Mendeleev.Web/Dockerfile -t mendeleev-app .
	docker build -f src/Mendeleev.Web/Dockerfile --target migrator -t mendeleev-migrator .
