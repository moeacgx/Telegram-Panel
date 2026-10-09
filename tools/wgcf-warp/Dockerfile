FROM python:3.12.12-slim-bookworm AS dependencies
ARG TARGETARCH
WORKDIR /build
COPY install.py dependencies.json ./
RUN python install.py "$TARGETARCH" /out

FROM python:3.12.12-slim-bookworm
WORKDIR /app
COPY --from=dependencies /out/ /usr/local/bin/
COPY warpctl.py THIRD_PARTY_NOTICES.txt ./
RUN mkdir /data && chown 10001:10001 /data
USER 10001:10001
ENTRYPOINT ["python", "/app/warpctl.py"]
CMD ["serve"]
