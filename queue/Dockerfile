FROM node:22-alpine
WORKDIR /app
COPY . .
ENV PORT=3000 QUEUE_DATA=/data/queue.json
VOLUME ["/data"]
EXPOSE 3000
CMD ["node", "server.js"]
