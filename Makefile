SOLUTION = MendeleevProject.slnx
DEV_COMPOSE = deploy/docker-compose.dev.yml

.PHONY: db-up db-down run build test test-unit test-integration test-contract test-volume test-alerts migration image

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

test-contract:  ## Remnawave adapter against the pinned panel in Docker; REMNAWAVE_TAG=x.y.z to try another version
	dotnet test tests/Mendeleev.ContractTests

test-volume:    ## 5000 users through the outbox into the pinned panel + reconciliation (a few minutes; docs/capacity.md)
	MENDELEEV_VOLUME=1 dotnet test tests/Mendeleev.ContractTests --filter "Category=Volume" --logger "console;verbosity=detailed"

test-alerts:    ## Prometheus rules (deploy/monitoring/alerts.test.yml) and the monitoring configs, in Docker
	docker run --rm --entrypoint /bin/promtool -v "$(CURDIR)/deploy/monitoring:/etc/prometheus:ro" prom/prometheus:v3.15.0 check config /etc/prometheus/prometheus.yml
	docker run --rm --entrypoint /bin/promtool -v "$(CURDIR)/deploy/monitoring:/etc/prometheus:ro" prom/prometheus:v3.15.0 test rules /etc/prometheus/alerts.test.yml
	docker run --rm --entrypoint /bin/amtool -v "$(CURDIR)/deploy/monitoring:/etc/alertmanager:ro" prom/alertmanager:v0.34.1 check-config /etc/alertmanager/alertmanager.yml

migration:      ## make migration NAME=AddSomething
	dotnet ef migrations add $(NAME) --project src/Mendeleev.Infrastructure --startup-project src/Mendeleev.Infrastructure --output-dir Database/Migrations

image:
	docker build -f src/Mendeleev.Web/Dockerfile -t mendeleev-app .
	docker build -f src/Mendeleev.Web/Dockerfile --target migrator -t mendeleev-migrator .
