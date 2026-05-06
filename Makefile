.PHONY: build publish run stop logs clean docker-build docker-up docker-down test-local test-competition test-full bench bench-test k6 k6-fraud download-resources preprocess preprocess-ivf preprocess-kmknn deploy

ARCH := $(shell uname -m)
ifeq ($(filter $(ARCH),arm64 aarch64),)
  RUNTIME_ID := linux-x64
  PLATFORM_FLAG := --platform linux/amd64
else
  RUNTIME_ID := linux-arm64
  PLATFORM_FLAG :=
endif

COMPOSE := docker compose -f docker/docker-compose.yml --project-directory docker
IMAGE := rinha/api:latest
REMOTE_HOST ?= maquinao
REMOTE_DIR ?= ~/rinha_backend_2026

INDEX_FORMAT ?= ivf
INDEX_CLUSTERS ?= 0
INDEX_NPROBE ?= 35

build:
	dotnet build src/Api/Api.csproj -c Release

# Generate index .bin files locally — once. Output goes to ./data which is
# mounted read-only into the API containers. Iterating on src/Api/ code does
# NOT trigger the 143s preprocess (the bin lives outside the docker image).
preprocess: preprocess-ivf preprocess-kmknn

preprocess-ivf:
	mkdir -p data
	dotnet run --project src/Api/Api.csproj -c Release -- \
		preprocess $(CURDIR)/resources/references.json.gz $(CURDIR)/data/ivf.bin \
		0 20 ivf 35
	@echo "  → data/ivf.bin (default 4096 clusters, nprobe 35)"

preprocess-kmknn:
	mkdir -p data
	dotnet run --project src/Api/Api.csproj -c Release -- \
		preprocess $(CURDIR)/resources/references.json.gz $(CURDIR)/data/kmknn-5k.bin \
		5000 20 kmknn 0
	@echo "  → data/kmknn-5k.bin (5000 clusters)"

publish:
	dotnet publish src/Api/Api.csproj -c Release -r $(RUNTIME_ID) -o ./out

docker-build:
	docker build $(PLATFORM_FLAG) --build-arg RUNTIME_ID=$(RUNTIME_ID) \
		--build-arg INDEX_FORMAT=$(INDEX_FORMAT) \
		--build-arg INDEX_CLUSTERS=$(INDEX_CLUSTERS) \
		--build-arg INDEX_NPROBE=$(INDEX_NPROBE) \
		-f docker/Dockerfile -t $(IMAGE) .

docker-up:
	$(COMPOSE) down 2>/dev/null || true
	$(COMPOSE) up -d
	@echo "Waiting for API to be ready..."
	@for i in $$(seq 1 60); do \
		curl -sf http://localhost:9999/ready > /dev/null 2>&1 && echo "  Ready after $${i}s" && break; \
		sleep 1; \
		[ $$i -eq 60 ] && echo "  TIMEOUT — API not ready after 60s" && exit 1 || true; \
	done

docker-down:
	$(COMPOSE) down

TEST_DATA ?= /tmp/test-data.json
API_URL ?= http://localhost:9999

test-local:
	./scripts/test-local.sh

test-competition:
	./scripts/test-competition.sh

test-full:
	@echo "Waiting for API..."
	@for i in $$(seq 1 60); do \
		curl -sf $(API_URL)/ready > /dev/null 2>&1 && echo "Ready after $${i}s" && break; \
		sleep 2; \
	done
	dotnet run --project src/Api/Api.csproj -c Release -- test $(API_URL) $(TEST_DATA)

# k6: drives load from a separate process. Pre-req: docker. Reads $(TEST_DATA)
# on host, mounts it into the k6 container.
K6_VUS      ?= 10
K6_DURATION ?= 30s
K6_RAMP_UP  ?= 5s

k6:
	@docker run --rm --network host \
		-v $(CURDIR)/scripts/k6:/scripts:ro \
		-v $(TEST_DATA):/test-data.json:ro \
		-e ENDPOINT="/fraud-score" \
		-e API_URL="$(API_URL)" \
		-e VUS="$(K6_VUS)" \
		-e DURATION="$(K6_DURATION)" \
		-e RAMP_UP="$(K6_RAMP_UP)" \
		grafana/k6 run /scripts/bench.js

k6-fraud: k6

bench:
	dotnet run --project tests/Benchmarks/Benchmarks.csproj -c Release

bench-test:
	dotnet test tests/Benchmarks/Benchmarks.csproj -c Release

run: docker-build docker-up
	@echo "Waiting for API..."
	@for i in $$(seq 1 60); do \
		curl -sf http://localhost:9999/ready > /dev/null 2>&1 && echo "Ready after $${i}s" && break; \
		sleep 2; \
	done

stop: docker-down

logs:
	$(COMPOSE) logs -f

download-resources:
	./scripts/download-resources.sh

clean:
	rm -rf out/
	dotnet clean src/Api/Api.csproj -c Release

deploy:
	rsync -avz --delete \
		--exclude='out/' \
		--exclude='.git/' \
		--exclude='bin/' \
		--exclude='obj/' \
		./ $(REMOTE_HOST):$(REMOTE_DIR)/
	@[ -f $(TEST_DATA) ] && rsync -avz $(TEST_DATA) $(REMOTE_HOST):$(TEST_DATA) && echo "Synced test data" || echo "No test data at $(TEST_DATA), skipping"
