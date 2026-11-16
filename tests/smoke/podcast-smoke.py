#!/usr/bin/env python3
# Podcast and audiobook playback end to end on an isolated server prepared by smoke.py: a file a
# feed publishes itself is described with the stream it carries, so Jellyfin can pick one to play.
# Without that stream Jellyfin answers "the audio stream is null" and playback stops at once.
import argparse
import json
import pathlib
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--state', default='.test-state/session.secret')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
config_path = '/Plugins/06423df8-a0cd-4ab9-a450-febb5ecb9133/Configuration'
viewer = state['users']['viewer']['token']
viewer_id = state['users']['viewer']['id']


def request(path, token, data=None, method=None):
    headers = {'Authorization': 'MediaBrowser Client="Jellystream Tests", Device="Integration", DeviceId="jellystream-podcast", Version="0.1.0"'
                                     + (', Token="' + token + '"' if token else ''), 'Content-Type': 'application/json'}
    req = urllib.request.Request(args.url + path, headers=headers, data=json.dumps(data).encode() if data is not None else None, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            raw = res.read()
            return res.status, json.loads(raw) if raw and res.headers.get_content_type() == 'application/json' else raw
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors='replace')[:300]


def ok(path, token, data=None, method=None):
    status, body = request(path, token, data, method)
    assert status < 400, (path.split('?')[0], status, body)
    return body


def children(parent):
    return ok('/Items?ParentId=' + parent + '&UserId=' + viewer_id + '&Fields=Overview,ProviderIds', viewer)['Items']


config = ok(config_path, state['admin'])
try:
    ok(config_path, state['admin'], dict(config, Enabled=True, PodcastEnabled=True, PodcastCurated=True,
                                        AudiobookEnabled=True), 'POST')
    channels = ok('/Channels?UserId=' + viewer_id, viewer)['Items']
    for name, wanted in (('Podcasts', 'Recently added'), ('Audiobooks', 'General Fiction')):
        channel = next((c for c in channels if c['Name'] == name), None)
        assert channel is not None, name + ' channel is missing'
        folders = children(channel['Id'])
        folder = next((f for f in folders if f['Name'] == wanted), folders[0] if folders else None)
        assert folder is not None, name + ' has no folders'
        items = children(folder['Id'])
        # An audiobook opens on its book, whose chapters are the playable parts.
        if name == 'Audiobooks':
            book = next((i for i in items if i.get('IsFolder')), None)
            assert book is not None, 'no audiobook listed: ' + str(items[:3])
            items = children(book['Id'])
        playable = next((i for i in items if i.get('Type') in ('Audio', 'AudioBook', 'Episode')), None)
        assert playable is not None, name + ' has no playable item: ' + str([(i.get('Name'), i.get('Type')) for i in items[:3]])
        info = ok('/Items/' + playable['Id'] + '/PlaybackInfo?UserId=' + viewer_id, viewer)
        sources = info.get('MediaSources') or []
        assert sources, name + ' resolved no source for ' + playable['Name']
        for source in sources:
            streams = source.get('MediaStreams') or []
            assert streams, name + ' source lacks media stream information: ' + str(source.get('Path'))
            assert any(s.get('Type') == 'Audio' for s in streams), name + ' source carries no audio stream: ' + str(streams)
        # The request Jellyfin makes when a client asks for the audio of a played item.
        status, body = request('/Audio/' + playable['Id'] + '/universal?userId=' + viewer_id + '&deviceId=test&container=mp3', viewer)
        assert status < 400, (name + ' universal audio request failed', status, body)
        print('PASS: ' + name + ' episode plays (' + playable['Name'][:48] + ')', flush=True)
finally:
    ok(config_path, state['admin'], config, 'POST')
