export function activate(ctx) {
  ctx.log.info("acme.hello activated");
  ctx.subscriptions.push(
    ctx.commands.register("acme.hello.run", async () => {
      await ctx.notifications?.show({
        title: "Hello from Octopus Plugin",
        body: "Your Jaunt JS plugin is running!"
      });
    })
  );
}

export function deactivate() {}
