# SSH tunnel for the Bot API (ТЗ 26, «Доступ к Bot API»; runbook docs/runbooks/bot-api-unreachable.md).
# Russian DPI drops TLS with the SNI api.telegram.org towards any address, so a plain HTTP or SOCKS proxy abroad
# does not help: the hop out of Russia has to be encrypted. The service talks SOCKS5 to this container.
FROM alpine:3.22
RUN apk add --no-cache openssh-client
ENTRYPOINT ["ssh"]
