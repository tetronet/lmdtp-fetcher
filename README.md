# LMDTP Fetcher
This is a simple program to allow browsers to join the Tetronet, which can be plenty useful sometimes.
### Base
This thing works on `ASP.NET 9.0` Framework and `ModemAPI`.
### Use cases
You found a convenient browser, like Firefox or something else, but there's no Tetronet support? This simple program will solve that problem in a few clicks.
### Why do you even need to use Tetronet?
Tetronet is a place to have fun. You can run your own LMDTP servers using ModemAPI and then other people will be able to access your page using this program. Also nothing is guarantied, but tetronet is kind of anonymous, but how I said, nothing is guarantied.
### How to use
1) Run it, no installation required
2) Create `config.txt` file in the same directory as the executable
3) Open in your web browser: http://127.0.0.1:8080/{server's tetronet address}/{lmdtp resource name}
4) This will make this program to make an OBTAIN request using the Tetronet and then it will stream the remote resource OBTAINed from the Tetronet to the HTTP socket straight into your web browser.
### Config
```
<rawws cias>
<http server port>
<lmdtp request time in milliseconds>
<maximum resource size>
```
### Config example
```
ws://192.168.0.121:30500/
8080
30000
268435456
```\
NOTE: don't forget to swap 192.168.0.121 with your actual CIAS for the Tetronet. Protocol is always RAWWS.
