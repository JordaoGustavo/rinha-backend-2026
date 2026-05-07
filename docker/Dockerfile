# Multi-stage build: download resources + build IVF index + AOT compile API.
# The image is fully self-contained — running it requires no host volumes.
# (lb/lb.c is an experimental round-robin TCP→UDS proxy in C. We A/B-tested
# it against haproxy:3.1-alpine and the custom proxy lost ~10% throughput
# at identical p99, so we ship haproxy. The C source stays in lb/ for
# future revisits — see README "Lessons learned".)

FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
ARG RUNTIME_ID=linux-x64
ARG RESOURCES_BASE_URL=https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/resources

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Reference data + lookup tables. Curl in the SAME RUN so the cache invalidates
# atomically when any of the three files change upstream.
RUN mkdir -p /resources \
 && curl -fsSL "$RESOURCES_BASE_URL/references.json.gz" -o /resources/references.json.gz \
 && curl -fsSL "$RESOURCES_BASE_URL/mcc_risk.json"      -o /resources/mcc_risk.json \
 && curl -fsSL "$RESOURCES_BASE_URL/normalization.json" -o /resources/normalization.json

COPY src/Api/ src/Api/

# Build IVF (clustered, int16 + bbox) AND exact (flat float32) indexes.
# Both are mmapped at runtime; the float32 reference enables exact rerank
# of top-N int16 candidates → 100% agreement with the brute-force oracle.
RUN mkdir -p /data \
 && dotnet run --project src/Api/Api.csproj -c Release -- \
        preprocess /resources/references.json.gz /data/ivf.bin 0 20 ivf 35 \
 && dotnet run --project src/Api/Api.csproj -c Release -- \
        preprocess /resources/references.json.gz /data/exact.bin 0 0 exact 0

# AOT publish for the target runtime.
RUN dotnet publish src/Api/Api.csproj -c Release -r ${RUNTIME_ID} -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-preview
WORKDIR /app
COPY --from=build /app .
COPY --from=build /resources/mcc_risk.json      /resources/mcc_risk.json
COPY --from=build /resources/normalization.json /resources/normalization.json
COPY --from=build /data/ivf.bin                 /data/ivf.bin
COPY --from=build /data/exact.bin               /data/exact.bin

ENV INDEX_PATH=/data/ivf.bin
ENV EXACT_PATH=/data/exact.bin
ENV RESOURCES_PATH=/resources
ENV API_PORT=8080
ENTRYPOINT ["./Api"]
