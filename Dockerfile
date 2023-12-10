FROM mcr.microsoft.com/dotnet/sdk:latest as builder
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        clang \
        make \
        cmake \
        zlib1g-dev \
        libicu-dev \
        libssl-dev 

COPY /src/* ./

RUN dotnet publish -r linux-x64 -c Release --ucr --sc -o out

FROM gcr.io/distroless/base-debian12:nonroot
WORKDIR /app

COPY --from=builder --chown=nonroot:nonroot --chmod=+x /app/out/vpm-build-repository .

ENTRYPOINT [ "/app/vpm-build-repository" ]
