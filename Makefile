.PHONY: download-resources download-k6-official preprocess preprocess-exact accuracy docker-build docker-build-dev docker-up docker-down k6 k6-varied k6-official clean

ARCH := $(shell uname -m)
ifeq ($(filter $(ARCH),arm64 aarch64),)
  RUNTIME_ID := linux-x64
  PLATFORM_FLAG := --platform linux/amd64
else
  RUNTIME_ID := linux-arm64
  PLATFORM_FLAG :=
endif

COMPOSE := docker compose -f docker/docker-compose.yml --project-directory docker
IMAGE   := rinha/api:latest

download-resources:
	./scripts/download-resources.sh

download-k6-official:
	./scripts/download-k6-official.sh

preprocess: download-resources
	mkdir -p data
	dotnet run --project src/Api/Api.csproj -c Release -- \
		preprocess $(CURDIR)/resources/references.json.gz $(CURDIR)/data/ivf.bin 0 20 ivf 8

docker-build:
	docker build $(PLATFORM_FLAG) --build-arg RUNTIME_ID=$(RUNTIME_ID) \
		-f docker/Dockerfile -t $(IMAGE) .

docker-build-dev:
	docker build $(PLATFORM_FLAG) --build-arg RUNTIME_ID=$(RUNTIME_ID) \
		-f docker/Dockerfile.dev -t $(IMAGE) .

docker-up:
	$(COMPOSE) up -d
	@echo "Waiting for /ready..."
	@for i in $$(seq 1 60); do \
		curl -sf http://localhost:9999/ready > /dev/null 2>&1 && echo "  ready in $${i}s" && break; \
		sleep 1; \
		[ $$i -eq 60 ] && echo "  TIMEOUT" && exit 1 || true; \
	done

docker-down:
	$(COMPOSE) down

K6_VUS      ?= 20
K6_DURATION ?= 60s

k6:
	docker run --rm --network host \
		-v $(CURDIR)/scripts/k6:/scripts:ro \
		-e API_URL="http://localhost:9999" \
		-e VUS="$(K6_VUS)" \
		-e DURATION="$(K6_DURATION)" \
		grafana/k6 run /scripts/bench.js

k6-varied:
	docker run --rm --network host \
		-v $(CURDIR)/scripts/k6:/scripts:ro \
		-e API_URL="http://localhost:9999" \
		-e VUS="$(K6_VUS)" \
		-e DURATION="$(K6_DURATION)" \
		grafana/k6 run /scripts/bench-varied.js

k6-official: download-k6-official
	docker run --rm --network host \
		-v $(CURDIR)/scripts/k6-official:/work:rw \
		-w /work \
		grafana/k6 run test/test.js
	@echo "--- score ---"
	@cat $(CURDIR)/scripts/k6-official/test/results.json | jq '{p99, scoring}' 2>/dev/null || true

preprocess-exact: download-resources
	mkdir -p data
	dotnet run --project src/Api/Api.csproj -c Release -- \
		preprocess $(CURDIR)/resources/references.json.gz $(CURDIR)/data/exact.bin 0 0 exact 0

ACC_COUNT ?= 10000
ACC_SEED  ?= 195842629

accuracy: preprocess preprocess-exact
	dotnet run --project src/Api/Api.csproj -c Release -- \
		accuracy $(CURDIR)/data/ivf.bin $(CURDIR)/data/exact.bin $(ACC_COUNT) $(ACC_SEED)

clean:
	rm -rf out/ data/
	dotnet clean src/Api/Api.csproj -c Release
